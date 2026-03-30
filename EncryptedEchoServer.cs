using System;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;

internal sealed class EncryptedEchoServer : EchoServerBase, IDisposable
{
    private readonly ILogger<EncryptedEchoServer> _logger;
    private readonly RSA _serverRSA;
    private bool _disposed;

    internal EncryptedEchoServer(ushort port) : base(port)
    {
        _logger = Settings.LoggerFactory.CreateLogger<EncryptedEchoServer>();
        _serverRSA = RSA.Create(2048);
        _logger.LogInformation("EncryptedEchoServer initialized with RSA key size: 2048 bits");
    }

    public override string GetServerHello()
    {
        _logger.LogInformation("Generating server hello message");
        byte[] publicKeyBytes = _serverRSA.ExportRSAPublicKey();
        string base64PublicKey = Convert.ToBase64String(publicKeyBytes);
        _logger.LogInformation($"Server public key (Base64): {base64PublicKey}");
        return base64PublicKey;
    }

    public override string TransformIncomingMessage(string input)
    {
        try
        {
            _logger.LogInformation("--- Processing Incoming Encrypted Message ---");
            _logger.LogInformation($"Received input: {input}");

            var message = JsonSerializer.Deserialize<EncryptedMessage>(input);
            
            _logger.LogInformation("Deserialized EncryptedMessage:");
            _logger.LogInformation($"AES Key Wrap (Base64): {Convert.ToBase64String(message.AesKeyWrap)}");
            _logger.LogInformation($"HMAC Key Wrap (Base64): {Convert.ToBase64String(message.HMACKeyWrap)}");
            _logger.LogInformation($"AES IV (Base64): {Convert.ToBase64String(message.AESIV)}");
            _logger.LogInformation($"Encrypted Message (Base64): {Convert.ToBase64String(message.Message)}");
            _logger.LogInformation($"HMAC (Base64): {Convert.ToBase64String(message.HMAC)}");

            _logger.LogInformation("Decrypting AES and HMAC keys");
            byte[] aesKey = _serverRSA.Decrypt(message.AesKeyWrap, RSAEncryptionPadding.OaepSHA256);
            byte[] hmacKey = _serverRSA.Decrypt(message.HMACKeyWrap, RSAEncryptionPadding.OaepSHA256);

            _logger.LogInformation("Decrypting message");
            byte[] decryptedMessage = DecryptWithAes(aesKey, message.AESIV, message.Message);

            _logger.LogInformation("Verifying HMAC");
            if (!VerifyHmac(hmacKey, decryptedMessage, message.HMAC))
            {
                _logger.LogWarning("HMAC verification failed");
                throw new InvalidSignatureException("The HMAC is invalid.");
            }
            _logger.LogInformation("HMAC verified successfully");

            string decodedMessage = Settings.Encoding.GetString(decryptedMessage);
            _logger.LogInformation($"Decoded message: {decodedMessage}");
            _logger.LogInformation("--- End of Incoming Message Processing ---");

            return decodedMessage;
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, $"Error deserializing the incoming message. Input: {input}");
            throw new InvalidDataException("Failed to deserialize the incoming message", ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error in TransformIncomingMessage. Input: {input}");
            throw;
        }
    }

    public override string TransformOutgoingMessage(string input)
    {
        try
        {
            _logger.LogInformation("--- Preparing Outgoing Signed Message ---");
            _logger.LogInformation($"Original message: {input}");

            byte[] messageBytes = Settings.Encoding.GetBytes(input);
            _logger.LogInformation($"Message bytes (Base64): {Convert.ToBase64String(messageBytes)}");

            _logger.LogInformation("Signing the message");
            byte[] signature = _serverRSA.SignData(messageBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
            _logger.LogInformation($"Signature (Base64): {Convert.ToBase64String(signature)}");

            var signedMessage = new SignedMessage { Message = messageBytes, Signature = signature };
            string jsonMessage = JsonSerializer.Serialize(signedMessage);
            
            _logger.LogInformation("Final JSON message:");
            _logger.LogInformation(jsonMessage);
            _logger.LogInformation("--- End of Outgoing Message Preparation ---");

            return jsonMessage;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in TransformOutgoingMessage");
            throw;
        }
    }

    private static byte[] DecryptWithAes(byte[] key, byte[] iv, byte[] encryptedData)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        aes.IV = iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;

        using var decryptor = aes.CreateDecryptor(aes.Key, aes.IV);
        return decryptor.TransformFinalBlock(encryptedData, 0, encryptedData.Length);
    }

    private static bool VerifyHmac(byte[] key, byte[] data, byte[] receivedHmac)
    {
        using var hmac = new HMACSHA256(key);
        byte[] computedHmac = hmac.ComputeHash(data);
        return CryptographicOperations.FixedTimeEquals(computedHmac, receivedHmac);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _serverRSA.Dispose();
            _disposed = true;
            _logger.LogInformation("EncryptedEchoServer disposed");
        }
        GC.SuppressFinalize(this);
    }
}