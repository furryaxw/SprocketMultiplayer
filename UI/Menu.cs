using UnityEngine;

namespace SprocketMultiplayer.UI
{
    public static class Menu
    {
        public class HandleClicks : MonoBehaviour { }
    }
}

public static class MenuActions
{
    private static readonly string fallbackNickname =
        "Player" + System.Guid.NewGuid().ToString("N").Substring(0, 6).ToUpperInvariant();

    public static void OnMultiplayerClick() { }

    public static string GetSteamNickname()
    {
        return fallbackNickname;
    }
}
