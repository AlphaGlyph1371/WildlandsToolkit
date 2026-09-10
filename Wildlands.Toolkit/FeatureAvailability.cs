namespace Wildlands.Toolkit;

internal static class FeatureAvailability
{
    // Keep the unfinished editor out of release builds until its character-item
    // pipeline has been proven in the game. All UI and direct entry points use
    // this switch, so re-enabling it is a deliberate one-line change.
    public static bool BuildTableEditor => false;
}
