using Winora.System.Licence;
using Xunit;

namespace Winora.System.Tests.Licence;

/// <summary>
/// Отпечаток машины: из чего он складывается и чего в нём быть не должно.
/// </summary>
/// <remarks>
/// От него зависит, будет ли работать купленный ключ, поэтому проверяется и то,
/// что он устойчив, и то, что он ничего не выдаёт наружу.
/// </remarks>
public sealed class HardwareIdTests
{
    private const string Guid = "6f2c1e40-8a1b-4d2e-9f3a-1c2b3d4e5f60";
    private const string Serial = "a4b1c2d3";

    [Fact]
    public void The_same_machine_always_gets_the_same_fingerprint()
    {
        Assert.Equal(HardwareId.Compute(Guid, Serial), HardwareId.Compute(Guid, Serial));
    }

    [Fact]
    public void A_different_machine_gets_a_different_one()
    {
        Assert.NotEqual(HardwareId.Compute(Guid, Serial), HardwareId.Compute(Guid, "ffffffff"));
        Assert.NotEqual(HardwareId.Compute(Guid, Serial), HardwareId.Compute("другой", Serial));
    }

    /// <summary>
    /// Серийные номера не видны в том, что уезжает на сервер.
    /// </summary>
    /// <remarks>
    /// Это и есть смысл хэша: украденная база лицензий не превращается в список
    /// того, у кого какое железо, а мы такой список и не собираем.
    /// </remarks>
    [Fact]
    public void The_hardware_itself_never_leaves_the_machine()
    {
        var fingerprint = HardwareId.Compute(Guid, Serial);

        Assert.DoesNotContain(Guid, fingerprint, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Serial, fingerprint, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Одной половины хватает.
    /// </summary>
    /// <remarks>
    /// MachineGuid переживает смену железа, серийник тома — переустановку Windows.
    /// Требовать оба значило бы, что машина, скрывающая серийник — так делают
    /// некоторые виртуальные диски, — не может получить лицензию вообще.
    /// </remarks>
    [Theory]
    [InlineData(Guid, "")]
    [InlineData("", Serial)]
    public void One_of_the_two_is_enough(string machineGuid, string volumeSerial)
    {
        Assert.NotEmpty(HardwareId.Compute(machineGuid, volumeSerial));
    }

    /// <summary>
    /// Ни одной половины — пустой ответ, а не отказ работать.
    /// </summary>
    /// <remarks>
    /// Сервер считает пустой отпечаток за «не проверять». Отказываться запускаться
    /// из-за неудавшегося чтения реестра значило бы наказывать человека за нашу
    /// осторожность.
    /// </remarks>
    [Fact]
    public void A_machine_that_says_nothing_gets_an_empty_fingerprint()
    {
        Assert.Empty(HardwareId.Compute("", ""));
        Assert.Empty(HardwareId.Compute("   ", "	"));
    }

    [Fact]
    public void The_fingerprint_is_short_enough_to_paste_into_a_message()
    {
        var fingerprint = HardwareId.Compute(Guid, Serial);

        Assert.Equal(32, fingerprint.Length);
        Assert.All(fingerprint, letter => Assert.Contains(letter, "0123456789abcdef"));
    }

    /// <summary>На этой машине отпечаток действительно читается.</summary>
    /// <remarks>
    /// Единственная проверка здесь, которая трогает Windows. Без неё все остальные
    /// продолжали бы проходить после опечатки в пути реестра — а купленные ключи
    /// перестали бы привязываться.
    /// </remarks>
    [Fact]
    public void This_machine_reports_a_fingerprint()
    {
        Assert.Equal(32, new HardwareId().Value.Length);
    }
}
