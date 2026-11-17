using SystemAutorun.Core.Models;

namespace SystemAutorun.Windows.Startup;

// Preserve the original GUID-only archive boundary and response/reason shape.
internal static class StartupArchiveSlotProbe
{
    internal static StartupArchiveSlotObservation Observe(string derivedArchivePath, CancellationToken cancellationToken = default,
        Action? beforeSeal = null)
    {
        var leafName = Path.GetFileName(derivedArchivePath);
        if (leafName.Length != 36 || !leafName.EndsWith(".lnk", StringComparison.Ordinal)
            || !Guid.TryParseExact(leafName[..32], "N", out var id) || id == Guid.Empty)
        { throw new ArgumentException("Only the derived original GUID archive slot may be observed."); }
        return StartupNativeSlotProbe.Observe(derivedArchivePath, cancellationToken, beforeSeal).ToArchive();
    }
}

// Only pass Resolve().Origin, derived from the fixed source + validated original leaf.
// This does not accept a target/working-directory path or parse a link's contents.
internal static class StartupOriginalSlotProbe
{
    internal static StartupOriginalSlotObservation Observe(string derivedOrigin, CancellationToken cancellationToken = default,
        Action? beforeSeal = null)
    {
        if (!Path.GetExtension(derivedOrigin).Equals(".lnk", StringComparison.OrdinalIgnoreCase))
        { throw new ArgumentException("Only the derived fixed-source shortcut slot may be observed."); }
        return StartupNativeSlotProbe.Observe(derivedOrigin, cancellationToken, beforeSeal).ToOriginal();
    }
}
