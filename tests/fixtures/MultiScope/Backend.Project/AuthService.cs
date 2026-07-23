namespace MultiScope.Backend;

/// <summary>Backend authentication service.</summary>
public sealed class AuthService
{
    public bool Validate(string token) => !string.IsNullOrEmpty(token);
}
