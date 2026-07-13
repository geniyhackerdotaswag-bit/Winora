using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Winora.Infrastructure.Persistence;

public sealed record JsonDocumentEnvelope<TPayload>(
    int SchemaVersion,
    DateTimeOffset CreatedUtc,
    string DocumentId,
    string PayloadSha256,
    TPayload Payload);

public sealed class JsonDocumentSerializer
{
    public const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.General)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public JsonDocumentEnvelope<TPayload> CreateEnvelope<TPayload>(
        string documentId,
        DateTimeOffset createdUtc,
        TPayload payload)
    {
        ValidateDocumentId(documentId);
        ValidateCreatedUtc(createdUtc);
        ArgumentNullException.ThrowIfNull(payload);

        return new JsonDocumentEnvelope<TPayload>(
            CurrentSchemaVersion,
            createdUtc,
            documentId,
            ComputePayloadHash(payload),
            payload);
    }

    public byte[] Serialize<TPayload>(JsonDocumentEnvelope<TPayload> envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        Validate(envelope);
        return JsonSerializer.SerializeToUtf8Bytes(envelope, SerializerOptions);
    }

    public JsonDocumentEnvelope<TPayload> DeserializeAndValidate<TPayload>(ReadOnlySpan<byte> utf8Json)
    {
        JsonDocumentEnvelope<TPayload>? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<JsonDocumentEnvelope<TPayload>>(
                utf8Json,
                SerializerOptions);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The persisted JSON document is malformed.", exception);
        }

        if (envelope is null)
        {
            throw new InvalidDataException("The persisted JSON document is empty.");
        }

        Validate(envelope);
        return envelope;
    }

    private static void Validate<TPayload>(JsonDocumentEnvelope<TPayload> envelope)
    {
        if (envelope.SchemaVersion != CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"Unsupported JSON schema version {envelope.SchemaVersion}.");
        }

        try
        {
            ValidateCreatedUtc(envelope.CreatedUtc);
            ValidateDocumentId(envelope.DocumentId);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException("The persisted JSON envelope metadata is invalid.", exception);
        }

        if (envelope.Payload is null)
        {
            throw new InvalidDataException("The persisted JSON payload is missing.");
        }

        var expectedHash = ComputePayloadHash(envelope.Payload);
        if (!StringComparer.Ordinal.Equals(expectedHash, envelope.PayloadSha256))
        {
            throw new InvalidDataException("The persisted JSON payload hash does not match its contents.");
        }
    }

    private static string ComputePayloadHash<TPayload>(TPayload payload)
    {
        var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(payload, SerializerOptions);
        return Convert.ToHexString(SHA256.HashData(payloadBytes));
    }

    private static void ValidateCreatedUtc(DateTimeOffset createdUtc)
    {
        if (createdUtc == default || createdUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("The creation timestamp must be a non-default UTC value.", nameof(createdUtc));
        }
    }

    private static void ValidateDocumentId(string documentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentId);
        if (documentId.Length > 256 ||
            documentId != documentId.Trim() ||
            documentId.Any(char.IsControl))
        {
            throw new ArgumentException("The stable document identifier is invalid.", nameof(documentId));
        }
    }
}
