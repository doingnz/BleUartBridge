using Plugin.BLE.Abstractions.Contracts;

namespace BPplus.Ble;

/// <summary>
/// Queries a connected BLE device's GATT services and matches against
/// <see cref="WellKnownBleProfiles.KnownProfiles"/> in priority order.
/// </summary>
public static class BleProfileDetector
{
    public static async Task<BleGattProfile?> DetectAsync(
        IDevice device, CancellationToken ct = default)
    {
        var services = await device.GetServicesAsync();

        foreach (var profile in WellKnownBleProfiles.KnownProfiles)
        {
            var service = services.FirstOrDefault(s => s.Id == profile.ServiceUuid);
            if (service == null) continue;

            var chars = await service.GetCharacteristicsAsync();

            bool hasTx = chars.Any(c => c.Id == profile.TxCharUuid &&
                (c.Properties.HasFlag(Plugin.BLE.Abstractions.CharacteristicPropertyType.Write) ||
                 c.Properties.HasFlag(Plugin.BLE.Abstractions.CharacteristicPropertyType.WriteWithoutResponse)));

            if (!hasTx) continue;

            if (!profile.Bidirectional)
            {
                bool hasRx = chars.Any(c => c.Id == profile.RxCharUuid &&
                    (c.Properties.HasFlag(Plugin.BLE.Abstractions.CharacteristicPropertyType.Notify) ||
                     c.Properties.HasFlag(Plugin.BLE.Abstractions.CharacteristicPropertyType.Indicate)));
                if (!hasRx) continue;
            }

            return profile;
        }

        return null;
    }
}
