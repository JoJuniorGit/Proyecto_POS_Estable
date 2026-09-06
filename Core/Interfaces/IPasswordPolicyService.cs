namespace Core.Interfaces;

public interface IPasswordPolicyService
{
    /// <summary>
    /// Valida una contraseña frente a la política de seguridad del sistema POS:
    /// - Longitud: 8 a 128 caracteres
    /// - Al menos 1 mayúscula, 1 minúscula, 1 número y 1 carácter especial
    /// - No contiene el nombre de usuario o cédula
    /// - No está en la lista negra de contraseñas comunes/triviales
    /// </summary>
    (bool IsValid, string? ErrorMessage) ValidatePassword(string password, string? username = null);

    /// <summary>
    /// Genera una contraseña temporal criptográficamente segura (CSPRNG)
    /// excluyendo caracteres visualmente ambiguos (0, O, o, 1, l, I, 8, B),
    /// garantizando el cumplimiento de la política de contraseñas.
    /// </summary>
    string GenerateSecureTemporaryPassword(int length = 12);
}
