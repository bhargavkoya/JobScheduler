namespace JobScheduler.Application.Auth;

public class ValidationException(string message) : Exception(message);
public class DuplicateEmailException(string email) : Exception($"Email '{email}' is already registered.");
public class InvalidCredentialsException() : Exception("Invalid email or password.");
public class UserNotFoundException(Guid id) : Exception($"User '{id}' was not found.");
