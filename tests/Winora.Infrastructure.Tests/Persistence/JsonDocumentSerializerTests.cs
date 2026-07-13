using System.Text.Json;
using System.Text.Json.Nodes;
using Winora.Infrastructure.Persistence;
using Xunit;

namespace Winora.Infrastructure.Tests.Persistence;

public sealed class JsonDocumentSerializerTests
{
    private static readonly DateTimeOffset CreatedUtc =
        new(2026, 7, 13, 8, 30, 0, TimeSpan.Zero);

    [Fact]
    public void Round_trip_preserves_required_metadata_and_payload()
    {
        var serializer = new JsonDocumentSerializer();
        var envelope = serializer.CreateEnvelope(
            "document-id",
            CreatedUtc,
            new ValuePayload(42));

        var bytes = serializer.Serialize(envelope);
        var roundTrip = serializer.DeserializeAndValidate<ValuePayload>(bytes);

        Assert.Equal(JsonDocumentSerializer.CurrentSchemaVersion, roundTrip.SchemaVersion);
        Assert.Equal(CreatedUtc, roundTrip.CreatedUtc);
        Assert.Equal("document-id", roundTrip.DocumentId);
        Assert.Matches("^[0-9A-F]{64}$", roundTrip.PayloadSha256);
        Assert.Equal(42, roundTrip.Payload.Value);
    }

    [Fact]
    public void Serialized_document_uses_the_owned_envelope_shape()
    {
        var serializer = new JsonDocumentSerializer();
        var bytes = serializer.Serialize(
            serializer.CreateEnvelope("document-id", CreatedUtc, new ValuePayload(42)));

        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;

        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("document-id", root.GetProperty("documentId").GetString());
        Assert.Equal(42, root.GetProperty("payload").GetProperty("value").GetInt32());
        Assert.Equal(64, root.GetProperty("payloadSha256").GetString()!.Length);
        Assert.True(root.TryGetProperty("createdUtc", out _));
    }

    [Fact]
    public void Payload_hash_corruption_is_rejected()
    {
        var serializer = new JsonDocumentSerializer();
        var bytes = serializer.Serialize(
            serializer.CreateEnvelope("document-id", CreatedUtc, new ValuePayload(1)));
        var root = JsonNode.Parse(bytes)!.AsObject();
        root["payload"]!["value"] = 2;

        Assert.Throws<InvalidDataException>(() =>
            serializer.DeserializeAndValidate<ValuePayload>(JsonSerializer.SerializeToUtf8Bytes(root)));
    }

    [Fact]
    public void Duplicate_json_properties_are_rejected_before_dto_materialization()
    {
        var serializer = new JsonDocumentSerializer();
        var bytes = serializer.Serialize(
            serializer.CreateEnvelope("document-id", CreatedUtc, new ValuePayload(1)));
        var json = System.Text.Encoding.UTF8.GetString(bytes).Replace(
            "\"value\":1",
            "\"value\":9,\"value\":1",
            StringComparison.Ordinal);

        Assert.Throws<InvalidDataException>(() =>
            serializer.DeserializeAndValidate<ValuePayload>(System.Text.Encoding.UTF8.GetBytes(json)));
    }

    [Fact]
    public void Unsupported_schema_is_rejected()
    {
        var serializer = new JsonDocumentSerializer();
        var bytes = serializer.Serialize(
            serializer.CreateEnvelope("document-id", CreatedUtc, new ValuePayload(1)));
        var root = JsonNode.Parse(bytes)!.AsObject();
        root["schemaVersion"] = JsonDocumentSerializer.CurrentSchemaVersion + 1;

        Assert.Throws<InvalidDataException>(() =>
            serializer.DeserializeAndValidate<ValuePayload>(JsonSerializer.SerializeToUtf8Bytes(root)));
    }

    [Fact]
    public void Non_utc_creation_time_is_rejected()
    {
        var serializer = new JsonDocumentSerializer();

        Assert.Throws<ArgumentException>(() =>
            serializer.CreateEnvelope(
                "document-id",
                new DateTimeOffset(2026, 7, 13, 8, 30, 0, TimeSpan.FromHours(3)),
                new ValuePayload(1)));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void Missing_stable_identifier_is_rejected(string documentId)
    {
        var serializer = new JsonDocumentSerializer();

        Assert.Throws<ArgumentException>(() =>
            serializer.CreateEnvelope(documentId, CreatedUtc, new ValuePayload(1)));
    }
}

internal sealed record ValuePayload(int Value);
