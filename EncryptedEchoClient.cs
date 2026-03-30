using System;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;

internal sealed class EncryptedEchoClient : EchoClientBase, IDisposable
{
    #region Fields and Constructor

    private readonly ILogger<EncryptedEchoClient> _logger;
    private RSA? _serverRSA;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the EncryptedEchoClient class.
    /// </summary>
    /// <param name="port">The port number to connect to.</param>
    /// <param name="address">The server address to connect to.</param>
    public EncryptedEchoClient(ushort port, string address) : base(port, address)
    {
        _logger = Settings.LoggerFactory.CreateLogger<EncryptedEchoClient>();
    }

    #endregion

    #region Public Methods

    /// <summary>
    /// Processes the server's hello message and imports the server's public key.
    /// </summary>
    /// <param name="message">The server's hello message containing the public key.</param>
    public override void ProcessServerHello(string message)
    {
        try
        {
            byte[] publicKeyBytes = Convert.FromBase64String(message);
            _serverRSA = RSA.Create();
            _serverRSA.ImportRSAPublicKey(publicKeyBytes, out _);
            _logger.LogInformation("Server public key successfully imported.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process server hello message");
            throw new CryptographicException("Invalid server public key", ex);
        }
    }

    /// <summary>
    /// Transforms the outgoing message by encrypting it using hybrid encryption.
    /// </summary>
    /// <param name="input">The input message to be encrypted.</param>
    /// <returns>A JSON string representing the encrypted message.</returns>
    public override string TransformOutgoingMessage(string input)
    {
        EnsureServerRsaInitialized();

        try
        {
            // Step 1: Prepare the message
            byte[] messageBytes = Settings.Encoding.GetBytes(input);

            // Step 2: Set up AES encryption
            using var aes = CreateAndConfigureAes();
            byte[] encryptedMessage = EncryptWithAes(aes, messageBytes);
            
            // Step 3: Generate and use HMAC
            byte[] hmacKey = GenerateRandomBytes(32);
            byte[] hmacValue = ComputeHmac(hmacKey, messageBytes);

            // Step 4: Encrypt the keys using RSA
            byte[] encryptedAesKey = EncryptWithRsa(aes.Key);
            byte[] encryptedHmacKey = EncryptWithRsa(hmacKey);

            // Step 5: Prepare the final encrypted message object
            var encryptedMessageObj = CreateEncryptedMessage(encryptedAesKey, aes.IV, encryptedMessage, encryptedHmacKey, hmacValue);
            
            // Step 6: Serialize and return
            return JsonSerializer.Serialize(encryptedMessageObj);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in TransformOutgoingMessage");
            throw;
        }
    }

    /// <summary>
    /// Transforms the incoming message by verifying its signature and decrypting it.
    /// </summary>
    /// <param name="input">The input JSON string representing the signed message.</param>
    /// <returns>The decrypted and verified message.</returns>
    public override string TransformIncomingMessage(string input)
    {
        EnsureServerRsaInitialized();

        try
        {
            // Step 1: Deserialize the signed message
            var signedMessage = JsonSerializer.Deserialize<SignedMessage>(input);
            
            // Step 2: Validate the signed message structure
            ValidateSignedMessage(signedMessage);
            
            // Step 3: Verify the signature
            VerifySignature(signedMessage);
            
            // Step 4: Return the decrypted message
            return Settings.Encoding.GetString(signedMessage.Message);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Error deserializing the incoming message");
            throw new InvalidDataException("Failed to deserialize the incoming message", ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in TransformIncomingMessage");
            throw;
        }
    }

    #endregion

    #region Private Helper Methods

    private void EnsureServerRsaInitialized()
    {
        if (_serverRSA == null)
        {
            throw new InvalidOperationException("Server RSA public key is not initialized.");
        }
    }

    private static Aes CreateAndConfigureAes()
    {
        var aes = Aes.Create();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        return aes;
    }

    private static byte[] EncryptWithAes(Aes aes, byte[] data)
    {
        using var encryptor = aes.CreateEncryptor(aes.Key, aes.IV);
        return encryptor.TransformFinalBlock(data, 0, data.Length);
    }

    private static byte[] GenerateRandomBytes(int length)
    {
        byte[] bytes = new byte[length];
        using (var rng = RandomNumberGenerator.Create())
        {
            rng.GetBytes(bytes);
        }
        return bytes;
    }

    private static byte[] ComputeHmac(byte[] key, byte[] data)
    {
        using var hmac = new HMACSHA256(key);
        return hmac.ComputeHash(data);
    }

    private byte[] EncryptWithRsa(byte[] data)
    {
        return _serverRSA!.Encrypt(data, RSAEncryptionPadding.OaepSHA256);
    }

    private static EncryptedMessage CreateEncryptedMessage(byte[] encryptedAesKey, byte[] iv, byte[] encryptedMessage, byte[] encryptedHmacKey, byte[] hmac)
    {
        return new EncryptedMessage
        {
            AesKeyWrap = encryptedAesKey,
            AESIV = iv,
            Message = encryptedMessage,
            HMACKeyWrap = encryptedHmacKey,
            HMAC = hmac
        };
    }

    private static void ValidateSignedMessage(SignedMessage signedMessage)
    {
        if (signedMessage.Message == null || signedMessage.Message.Length == 0 ||
            signedMessage.Signature == null || signedMessage.Signature.Length == 0)
        {
            throw new InvalidDataException("Deserialized signed message is invalid or incomplete");
        }
    }

    private void VerifySignature(SignedMessage signedMessage)
    {
        bool isValid = _serverRSA!.VerifyData(
            signedMessage.Message,
            signedMessage.Signature,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pss);

        if (!isValid)
        {
            throw new InvalidSignatureException("The signature is invalid.");
        }
    }

    #endregion

    #region IDisposable Implementation

    public void Dispose()
    {
        if (!_disposed)
        {
            _serverRSA?.Dispose();
            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }

    #endregion
}