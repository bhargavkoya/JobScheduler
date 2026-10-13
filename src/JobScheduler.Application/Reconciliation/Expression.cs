using System.Globalization;
using JobScheduler.Application.Auth;

namespace JobScheduler.Application.Reconciliation;

/// <summary>A reference to a column of an uploaded file, written <c>alias.Column</c> or <c>alias.[Column With Spaces]</c>.</summary>
public sealed record ColumnRef(string Alias, string Column);

/// <summary>
/// A small arithmetic expression over file columns: numbers, column references, + - * /, parentheses and unary minus.
/// Hand-written parser and evaluator, so rules stay data and nothing is ever compiled or executed as code.
/// </summary>
public sealed class Expression
{
    private readonly Node _root;

    private Expression(Node root, IReadOnlyList<ColumnRef> references)
    {
        _root = root;
        References = references;
    }

    /// <summary>Every distinct column the expression reads (used to check the rule against the file headers).</summary>
    public IReadOnlyList<ColumnRef> References { get; }

    public static Expression Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new ValidationException("An expression is empty.");
        var parser = new Parser(text);
        var root = parser.ParseAll();
        return new Expression(root, parser.References);
    }

    /// <summary>
    /// Evaluates with decimal arithmetic. <paramref name="resolve"/> supplies each column's numeric value and should throw
    /// <see cref="CellValueException"/> when a cell is not a number. Divide-by-zero surfaces as <see cref="DivideByZeroException"/>.
    /// </summary>
    public decimal Evaluate(Func<ColumnRef, decimal> resolve) => _root.Eval(resolve);

    private abstract record Node
    {
        public abstract decimal Eval(Func<ColumnRef, decimal> resolve);
    }

    private sealed record Num(decimal Value) : Node
    {
        public override decimal Eval(Func<ColumnRef, decimal> resolve) => Value;
    }

    private sealed record Ref(ColumnRef Column) : Node
    {
        public override decimal Eval(Func<ColumnRef, decimal> resolve) => resolve(Column);
    }

    private sealed record Neg(Node Operand) : Node
    {
        public override decimal Eval(Func<ColumnRef, decimal> resolve) => -Operand.Eval(resolve);
    }

    private sealed record Bin(char Op, Node Left, Node Right) : Node
    {
        public override decimal Eval(Func<ColumnRef, decimal> resolve)
        {
            var l = Left.Eval(resolve);
            var r = Right.Eval(resolve);
            return Op switch
            {
                '+' => l + r,
                '-' => l - r,
                '*' => l * r,
                _ => l / r
            };
        }
    }

    private sealed class Parser(string text)
    {
        private int _pos;
        private readonly List<ColumnRef> _refs = [];

        public IReadOnlyList<ColumnRef> References => _refs;

        public Node ParseAll()
        {
            var node = ParseSum();
            SkipSpaces();
            if (_pos < text.Length) throw Error($"Unexpected '{text[_pos]}'");
            return node;
        }

        private Node ParseSum()
        {
            var left = ParseProduct();
            while (true)
            {
                SkipSpaces();
                if (Peek() is '+' or '-')
                {
                    var op = text[_pos++];
                    left = new Bin(op, left, ParseProduct());
                }
                else return left;
            }
        }

        private Node ParseProduct()
        {
            var left = ParseUnary();
            while (true)
            {
                SkipSpaces();
                if (Peek() is '*' or '/')
                {
                    var op = text[_pos++];
                    left = new Bin(op, left, ParseUnary());
                }
                else return left;
            }
        }

        private Node ParseUnary()
        {
            SkipSpaces();
            if (Peek() == '-') { _pos++; return new Neg(ParseUnary()); }
            if (Peek() == '+') { _pos++; return ParseUnary(); }
            return ParsePrimary();
        }

        private Node ParsePrimary()
        {
            SkipSpaces();
            var c = Peek();
            if (c == '(')
            {
                _pos++;
                var inner = ParseSum();
                SkipSpaces();
                if (Peek() != ')') throw Error("Missing closing parenthesis");
                _pos++;
                return inner;
            }

            if (char.IsAsciiDigit(c) || c == '.') return ParseNumber();
            if (char.IsAsciiLetter(c) || c == '_') return ParseReference();
            throw Error(c == '\0' ? "The expression ends too early" : $"Unexpected '{c}'");
        }

        private Node ParseNumber()
        {
            var start = _pos;
            while (_pos < text.Length && (char.IsAsciiDigit(text[_pos]) || text[_pos] == '.')) _pos++;
            var token = text[start.._pos];
            if (!decimal.TryParse(token, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value))
                throw Error($"'{token}' is not a valid number", start);
            return new Num(value);
        }

        private Node ParseReference()
        {
            var start = _pos;
            while (_pos < text.Length && (char.IsAsciiLetterOrDigit(text[_pos]) || text[_pos] == '_')) _pos++;
            var alias = text[start.._pos];

            if (Peek() != '.')
                throw Error($"'{alias}' must be written as {alias}.ColumnName (a file alias, a dot, then the column)", start);
            _pos++;

            string column;
            if (Peek() == '[')
            {
                var close = text.IndexOf(']', _pos + 1);
                if (close < 0) throw Error("Missing closing ] in a column name");
                column = text[(_pos + 1)..close].Trim();
                _pos = close + 1;
            }
            else
            {
                var colStart = _pos;
                while (_pos < text.Length && (char.IsAsciiLetterOrDigit(text[_pos]) || text[_pos] == '_')) _pos++;
                column = text[colStart.._pos];
            }

            if (column.Length == 0) throw Error($"Missing column name after '{alias}.'");

            var reference = new ColumnRef(alias, column);
            if (!_refs.Any(r => string.Equals(r.Alias, alias, StringComparison.OrdinalIgnoreCase)
                                && string.Equals(r.Column, column, StringComparison.OrdinalIgnoreCase)))
                _refs.Add(reference);
            return new Ref(reference);
        }

        private char Peek() => _pos < text.Length ? text[_pos] : '\0';

        private void SkipSpaces()
        {
            while (_pos < text.Length && char.IsWhiteSpace(text[_pos])) _pos++;
        }

        private ValidationException Error(string message, int? at = null) =>
            new($"Invalid expression \"{text}\": {message} (position {(at ?? _pos) + 1}). " +
                "Use column references like file1.Amount, or file1.[Net Amount] for names with spaces.");
    }
}

/// <summary>A cell could not be used as a number. The message names the column and the offending value.</summary>
public sealed class CellValueException(string message) : Exception(message);

public static class CellNumber
{
    /// <summary>Plain decimals with optional sign and thousands separators ("1,234.50"). Currency symbols and (negatives) are rejected.</summary>
    public static decimal Parse(string? raw, string columnLabel)
    {
        var text = raw?.Trim() ?? "";
        if (text.Length == 0) throw new CellValueException($"{columnLabel} is empty");
        if (!decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowThousands,
                CultureInfo.InvariantCulture, out var value))
            throw new CellValueException($"{columnLabel} is not a number ('{Shorten(text)}')");
        return value;
    }

    private static string Shorten(string s) => s.Length <= 30 ? s : s[..30] + "...";
}
