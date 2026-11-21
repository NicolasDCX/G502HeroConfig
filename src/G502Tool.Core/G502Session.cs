using G502Tool.Core.Hidpp;
using G502Tool.Core.Model;

namespace G502Tool.Core;

public enum CheckStatus { Verified, Failed, NotChecked }

public sealed record VerifyCheck(string What, CheckStatus Status, string Detail);

public sealed class OperationResult
{
    public List<VerifyCheck> Checks { get; } = new();
    public string Title { get; set; } = "";

    /// <summary>True only if at least one check was read back from the device and none failed.</summary>
    public bool Success => Checks.Any(c => c.Status == CheckStatus.Verified) && Checks.All(c => c.Status != CheckStatus.Failed);

    public void Ok(string what, string detail) => Checks.Add(new(what, CheckStatus.Verified, detail));
    public void Fail(string what, string detail) => Checks.Add(new(what, CheckStatus.Failed, detail));
    public void Skip(string what, string detail) => Checks.Add(new(what, CheckStatus.NotChecked, detail));
}

public sealed class ProfileView
{
    public int Index { get; init; }              // 0-based
    public ushort UserSector => (ushort)(Index + 1);
    public bool Enabled { get; init; }           // listed as enabled in the directory
    public bool FromFactory { get; init; }       // no valid user sector: showing the read-only ROM copy
    public ushort SourceSector { get; init; }
    public required ProfileSector Sector { get; init; }
    public string DisplayName => $"Profile {Index + 1}" + (Sector.Name is { } n ? $" – {n}" : "");
}

public sealed class DeviceSnapshot
{
    public required string DeviceName { get; init; }
    public string? Firmware { get; init; }
    public int ProtocolMajor { get; init; }
    public int ProtocolMinor { get; init; }
    public required IReadOnlyDictionary<ushort, byte> Features { get; init; }

    public DpiCapabilities? DpiCaps { get; init; }
    public int? LiveDpi { get; init; }
    public IReadOnlyList<int> SupportedIntervalsMs { get; init; } = Array.Empty<int>();
    public int? LiveIntervalMs { get; init; }

    public OnboardProfilesInfo? Info { get; init; }
    public OnboardMode Mode { get; init; }
    public ushort ActiveSector { get; init; }
    public int? ActiveDpiIndex { get; init; }
    public bool DirectoryValid { get; init; }
    public List<ProfileView> Profiles { get; init; } = new();
    public ProfileSector? Factory { get; init; }

    public bool HasOnboardProfiles => Info != null;
    public int ButtonCount => Info == null ? 0 : Math.Min(16, Info.ButtonCount);
    public bool ActiveIsFactory => (ActiveSector & 0xFF00) != 0;
    public int ActiveProfileIndex => (ActiveSector & 0xFF) - 1;
}

/// <summary>A set of field edits for one profile. Only these bytes change on save.</summary>
public sealed class ProfileEdit
{
    public Dictionary<int, int> Dpi { get; } = new();
    public int? DefaultDpiIndex { get; set; }
    public int? ReportIntervalMs { get; set; }
    public Dictionary<int, ButtonBinding> Buttons { get; } = new();
    public Dictionary<int, byte[]> Leds { get; } = new();

    public bool IsEmpty => Dpi.Count == 0 && DefaultDpiIndex == null && ReportIntervalMs == null && Buttons.Count == 0 && Leds.Count == 0;

    public void ApplyTo(ProfileSector s)
    {
        foreach (var (slot, dpi) in Dpi) s.SetDpi(slot, dpi);
        if (DefaultDpiIndex is int d) s.DefaultDpiIndex = d;
        if (ReportIntervalMs is int r) s.ReportIntervalMs = r;
        foreach (var (i, b) in Buttons) s.SetButton(i, b);
        foreach (var (z, led) in Leds) s.SetLed(z, led);
    }

    public List<string> Validate(DeviceSnapshot snap, ProfileSector baseSector)
    {
        var errors = new List<string>();
        foreach (var (slot, dpi) in Dpi)
        {
            if (slot is < 0 or >= ProfileSector.DpiSlots) errors.Add($"DPI stage {slot + 1} does not exist.");
            else if (!ProfileSector.IsStageUsed(baseSector.GetDpi(slot))) errors.Add($"DPI stage {slot + 1} is not enabled on this profile.");
            else if (snap.DpiCaps != null && !snap.DpiCaps.IsValid(dpi)) errors.Add($"{dpi} DPI is not supported (valid: {snap.DpiCaps.Describe()}).");
            else if (snap.DpiCaps == null && dpi is < 100 or > 25600) errors.Add($"{dpi} DPI is out of range.");
        }
        if (DefaultDpiIndex is int di && (di is < 0 or >= ProfileSector.DpiSlots || !ProfileSector.IsStageUsed(baseSector.GetDpi(di))))
            errors.Add("Default DPI stage must be an enabled stage.");
        if (ReportIntervalMs is int ms && snap.SupportedIntervalsMs.Count > 0 && !snap.SupportedIntervalsMs.Contains(ms))
            errors.Add($"Polling interval {ms} ms is not supported by this mouse.");
        foreach (var (i, b) in Buttons)
        {
            if (i < 0 || i >= snap.ButtonCount) errors.Add($"Button {i + 1} does not exist.");
            if (IsPrimaryClickSlot(snap, i) && !(b.Kind == BindingKind.MouseButton && b.MouseButtonNumber == 1))
                errors.Add("The primary (left) button cannot be remapped by this tool, so you cannot lock yourself out.");
        }
        foreach (var z in Leds.Keys)
            if (z is < 0 or > 1) errors.Add($"LED zone {z + 1} does not exist.");
        return errors;
    }

    public static bool IsPrimaryClickSlot(DeviceSnapshot snap, int index)
    {
        if (snap.Factory != null)
        {
            var f = snap.Factory.GetButton(index);
            return f.Kind == BindingKind.MouseButton && f.MouseButtonNumber == 1;
        }
        return index == 0;
    }
}

public sealed class FactoryCopyRequiredException : Exception
{
    public FactoryCopyRequiredException() : base(
        "This profile only exists as the read-only factory profile. Saving creates editable copies of the factory profiles in onboard memory.") { }
}

public sealed class ConcurrentChangeException : Exception
{
    public ConcurrentChangeException() : base(
        "The profile on the mouse changed since it was loaded (another program such as G HUB may have written it). Nothing was written. Reload and try again.") { }
}

/// <summary>All device operations for one connected G502 HERO. Not thread-safe; serialize calls.</summary>
public sealed class G502Session
{
    public HidppDevice Device { get; }

    public G502Session(HidppDevice device) => Device = device;

    public DeviceSnapshot ReadSnapshot(string fallbackName)
    {
        var d = Device;
        var (maj, min) = d.GetProtocolVersion();
        if (maj < 2) throw new HidppException($"Device speaks HID++ {maj}.{min}; 2.0 or newer is required.");
        foreach (var f in new[] { FeatureId.DeviceInfo, FeatureId.DeviceName, FeatureId.AdjustableDpi, FeatureId.ReportRate, FeatureId.OnboardProfiles })
            d.LookupFeature(f);

        string name = Try(() => DeviceInfoFeature.GetName(d)) ?? fallbackName;
        string? fw = Try(() => DeviceInfoFeature.GetFirmware(d));

        DpiCapabilities? caps = null; int? liveDpi = null;
        if (d.Has(FeatureId.AdjustableDpi))
        {
            caps = AdjustableDpiFeature.GetCapabilities(d);
            liveDpi = AdjustableDpiFeature.GetDpi(d);
        }

        IReadOnlyList<int> intervals = Array.Empty<int>(); int? liveInterval = null;
        if (d.Has(FeatureId.ReportRate))
        {
            intervals = ReportRateFeature.GetSupportedIntervalsMs(d);
            liveInterval = ReportRateFeature.GetIntervalMs(d);
        }

        OnboardProfilesInfo? info = null; OnboardMode mode = OnboardMode.Unknown; ushort active = 0; int? dpiIdx = null;
        bool dirValid = false; var profiles = new List<ProfileView>(); ProfileSector? factory = null;
        if (d.Has(FeatureId.OnboardProfiles))
        {
            info = OnboardProfilesFeature.GetInfo(d);
            if (info.MemoryModel != 0x01 || info.MacroFormat != 0x01 || info.ProfileFormat is < 1 or > 5 || info.SectorSize < ProfileSector.MinSupportedSize)
                throw new HidppException(
                    $"Unexpected onboard memory layout (memory 0x{info.MemoryModel:X2}, profile format 0x{info.ProfileFormat:X2}, sector {info.SectorSize} bytes). Refusing to edit profiles.");
            mode = OnboardProfilesFeature.GetMode(d);
            active = OnboardProfilesFeature.GetCurrentProfile(d);
            dpiIdx = Try(() => (int?)OnboardProfilesFeature.GetCurrentDpiIndex(d));

            var dir = new ProfileDirectory(ReadChecked(d,OnboardProfilesFeature.DirectorySector, info.SectorSize));
            dirValid = dir.CrcValid;
            if (info.RomProfileCount > 0)
                factory = new ProfileSector(ReadChecked(d,(ushort)(OnboardProfilesFeature.RomBase + 1), info.SectorSize));

            for (int i = 0; i < info.ProfileCount; i++)
            {
                ushort userSector = (ushort)(i + 1);
                var entry = dir.Entries.FirstOrDefault(e => e.Sector == userSector);
                ProfileSector? user = null;
                if (dirValid && entry != null)
                {
                    var s = new ProfileSector(ReadChecked(d,userSector, info.SectorSize));
                    if (s.CrcValid) user = s;
                }
                if (user != null)
                {
                    profiles.Add(new ProfileView { Index = i, Enabled = entry!.Enabled, FromFactory = false, SourceSector = userSector, Sector = user });
                }
                else
                {
                    ushort rom = RomSectorFor(info, i);
                    var s = rom == OnboardProfilesFeature.RomBase + 1 && factory != null
                        ? factory.Clone()
                        : new ProfileSector(ReadChecked(d,rom, info.SectorSize));
                    profiles.Add(new ProfileView { Index = i, Enabled = !dirValid && i < Math.Max(1, info.RomProfileCount), FromFactory = true, SourceSector = rom, Sector = s });
                }
            }
        }

        return new DeviceSnapshot
        {
            DeviceName = name, Firmware = fw, ProtocolMajor = maj, ProtocolMinor = min,
            Features = new Dictionary<ushort, byte>(d.Features),
            DpiCaps = caps, LiveDpi = liveDpi, SupportedIntervalsMs = intervals, LiveIntervalMs = liveInterval,
            Info = info, Mode = mode, ActiveSector = active, ActiveDpiIndex = dpiIdx, DirectoryValid = dirValid,
            Profiles = profiles, Factory = factory,
        };
    }

    static ushort RomSectorFor(OnboardProfilesInfo info, int index)
        => (ushort)(OnboardProfilesFeature.RomBase + (index < info.RomProfileCount ? index + 1 : 1));

    /// <summary>RAM-only DPI change via 0x2201, read back with getSensorDpi.</summary>
    public OperationResult ApplyTemporaryDpi(int dpi)
    {
        var r = new OperationResult { Title = $"Temporary DPI {dpi}" };
        AdjustableDpiFeature.SetDpi(Device, dpi);
        int live = AdjustableDpiFeature.GetDpi(Device);
        if (live == dpi) r.Ok("Live sensor DPI", $"Mouse reports {live} DPI. Temporary: lost when you switch DPI stage or profile, or unplug.");
        else r.Fail("Live sensor DPI", $"Wrote {dpi} but the mouse reports {live}.");
        return r;
    }

    public OperationResult SetOnboardMode()
    {
        var r = new OperationResult { Title = "Switch to onboard memory mode" };
        OnboardProfilesFeature.SetMode(Device, OnboardMode.Onboard);
        var m = OnboardProfilesFeature.GetMode(Device);
        if (m == OnboardMode.Onboard) r.Ok("Mode", "Mouse reports onboard memory mode: it now uses the profiles stored on the mouse.");
        else r.Fail("Mode", $"Mouse still reports mode {m}.");
        return r;
    }

    public OperationResult ActivateProfile(DeviceSnapshot snap, int index)
    {
        var r = new OperationResult { Title = $"Activate profile {index + 1}" };
        var pv = snap.Profiles[index];
        if (pv.FromFactory || !pv.Enabled)
        {
            r.Fail("Profile", "This profile is not enabled in onboard memory, so the mouse cannot switch to it.");
            return r;
        }
        OnboardProfilesFeature.SetCurrentProfile(Device, pv.UserSector);
        var now = OnboardProfilesFeature.GetCurrentProfile(Device);
        if (now == pv.UserSector) r.Ok("Active profile", $"Mouse reports profile {index + 1} active.");
        else r.Fail("Active profile", $"Mouse reports sector 0x{now:X4} instead of 0x{pv.UserSector:X4}.");
        int live = Device.Has(FeatureId.AdjustableDpi) ? AdjustableDpiFeature.GetDpi(Device) : 0;
        if (live > 0) r.Ok("Live DPI", $"{live} DPI after the switch.");
        if (snap.Mode != OnboardMode.Onboard) r.Skip("Mode", "Mouse is in host mode; the profile's settings only take effect in onboard memory mode.");
        return r;
    }

    /// <summary>
    /// Read-modify-write of one profile in onboard memory, then byte-for-byte read-back and live checks.
    /// </summary>
    public OperationResult SaveProfile(DeviceSnapshot snap, int index, ProfileEdit edit, bool allowFactoryCopy)
    {
        var d = Device;
        var info = snap.Info ?? throw new HidppException("This mouse does not expose onboard profiles (feature 0x8100).");
        var pv = snap.Profiles[index];
        var r = new OperationResult { Title = $"Save profile {index + 1} to onboard memory" };
        int size = info.SectorSize;
        ushort target = pv.UserSector;

        // 1. Read the current state again, right before writing.
        var dir = new ProfileDirectory(ReadChecked(d,OnboardProfilesFeature.DirectorySector, size));
        ProfileSector? fresh = null;
        if (dir.CrcValid && dir.Entries.Any(e => e.Sector == target))
        {
            var s = new ProfileSector(ReadChecked(d,target, size));
            if (s.CrcValid) fresh = s;
        }
        bool fromFactory = fresh == null;
        if (fromFactory)
        {
            if (!allowFactoryCopy) throw new FactoryCopyRequiredException();
            fresh = new ProfileSector(ReadChecked(d,RomSectorFor(info, index), size));
        }

        // 2. Refuse if the device changed under us.
        if (!fresh!.Bytes.AsSpan().SequenceEqual(pv.Sector.Bytes) || fromFactory != pv.FromFactory)
            throw new ConcurrentChangeException();

        var errors = edit.Validate(snap, fresh);
        if (errors.Count > 0) throw new ArgumentException(string.Join(Environment.NewLine, errors));

        // 3. Apply only the edited fields.
        var updated = fresh.Clone();
        edit.ApplyTo(updated);
        updated.UpdateCrc();
        var changed = ProfileSector.Diff(fresh.Bytes, updated.Bytes).Where(o => o < size - 2).ToList();
        if (changed.Count == 0 && !fromFactory)
        {
            r.Skip("Onboard memory", "No changes compared to what is stored on the mouse; nothing was written.");
            return r;
        }
        d.Trace.Note($"Saving sector 0x{target:X4}; changed byte offsets: {string.Join(",", changed)}");

        // 4. Write, then read back and compare every byte.
        WriteVerified(r, target, updated.Bytes, $"Profile {index + 1}");

        // 5. First save on a mouse still using factory profiles: copy the others and create a directory.
        if (fromFactory)
        {
            var entries = new List<ProfileDirectory.Entry>();
            if (dir.CrcValid)
            {
                entries.AddRange(dir.Entries.Where(e => e.Sector != target));
                entries.Add(new(target, true));
                entries.Sort((a, b) => a.Sector.CompareTo(b.Sector));
            }
            else
            {
                for (int i = 0; i < info.ProfileCount; i++)
                {
                    ushort us = (ushort)(i + 1);
                    if (us != target)
                    {
                        var copy = new ProfileSector(ReadChecked(d,RomSectorFor(info, i), size));
                        copy.UpdateCrc();
                        WriteVerified(r, us, copy.Bytes, $"Profile {i + 1} (copied from factory)");
                    }
                    entries.Add(new(us, us == target || i < Math.Max(1, info.RomProfileCount)));
                }
            }
            WriteVerified(r, OnboardProfilesFeature.DirectorySector, ProfileDirectory.Build(size, entries), "Profile directory");
        }

        if (r.Checks.Any(c => c.Status == CheckStatus.Failed)) return r;

        // 6. Live checks when this is (or replaces) the active profile.
        bool isActive = snap.ActiveSector == target || (snap.ActiveIsFactory && fromFactory && snap.ActiveSector == RomSectorFor(info, index));
        if (!isActive)
        {
            r.Skip("Live settings", $"Profile {index + 1} is not the active profile, so its settings are not live. Activate it to use them.");
            return r;
        }
        if (snap.Mode != OnboardMode.Onboard)
        {
            r.Skip("Live settings", "Mouse is in host (software) mode, so onboard settings are not live. Switch to onboard mode or replug the mouse.");
            return r;
        }

        int prevIdx = snap.ActiveDpiIndex ?? updated.DefaultDpiIndex;
        OnboardProfilesFeature.SetCurrentProfile(d, target); // reloads the profile from memory
        var nowActive = OnboardProfilesFeature.GetCurrentProfile(d);
        if (nowActive != target) r.Fail("Reload profile", $"Mouse reports sector 0x{nowActive:X4} active instead of 0x{target:X4}.");

        int idx = prevIdx is >= 0 and < ProfileSector.DpiSlots && ProfileSector.IsStageUsed(updated.GetDpi(prevIdx)) ? prevIdx : updated.DefaultDpiIndex;
        Try(() => { OnboardProfilesFeature.SetCurrentDpiIndex(d, idx); return 0; });
        int? liveIdx = Try(() => (int?)OnboardProfilesFeature.GetCurrentDpiIndex(d));
        if (liveIdx is int li and >= 0 and < ProfileSector.DpiSlots) idx = li;

        if (d.Has(FeatureId.AdjustableDpi))
        {
            int live = AdjustableDpiFeature.GetDpi(d);
            int expected = updated.GetDpi(idx);
            if (live == expected) r.Ok("Live DPI", $"Mouse is using stage {idx + 1} at {live} DPI, matching the saved value.");
            else r.Fail("Live DPI", $"Stage {idx + 1} is saved as {expected} DPI but the sensor reports {live}. Unplug and reconnect the mouse, then reload to check again.");
        }
        if (edit.ReportIntervalMs is int ms && d.Has(FeatureId.ReportRate))
        {
            int live = ReportRateFeature.GetIntervalMs(d);
            if (live == ms) r.Ok("Live polling rate", $"{1000 / ms} Hz.");
            else r.Fail("Live polling rate", $"Saved {1000 / ms} Hz but the mouse reports {1000 / Math.Max(1, live)} Hz.");
        }
        if (edit.Buttons.Count > 0) r.Skip("Button behaviour", "Bindings were verified in memory; press the buttons to confirm (the mouse cannot report a button's live action).");
        if (edit.Leds.Count > 0) r.Skip("Lighting", "Lighting was verified in memory; check the mouse visually.");
        return r;
    }

    void WriteVerified(OperationResult r, ushort sector, byte[] data, string label)
    {
        OnboardProfilesFeature.WriteSector(Device, sector, data);
        byte[] back;
        try { back = ReadChecked(Device, sector, data.Length); }
        catch (HidppException e)
        {
            r.Fail($"{label}: onboard memory", $"Sector 0x{sector:X4} was written but could not be read back reliably: {e.Message}");
            return;
        }
        var diff = ProfileSector.Diff(data, back);
        if (diff.Count == 0) r.Ok($"{label}: onboard memory", $"Read back all {data.Length} bytes of sector 0x{sector:X4}; they match what was written.");
        else r.Fail($"{label}: onboard memory", $"Read-back of sector 0x{sector:X4} differs at {diff.Count} byte(s) (first at offset {diff[0]}).");
    }

    /// <summary>Every sector read goes through the consistency check; see <see cref="OnboardProfilesFeature.ReadSectorChecked"/>.</summary>
    static byte[] ReadChecked(HidppDevice d, ushort sector, int size)
        => OnboardProfilesFeature.ReadSectorChecked(d, sector, size).Bytes;

    static T? Try<T>(Func<T?> f)
    {
        try { return f(); }
        catch (HidppException) { return default; }
    }
}
