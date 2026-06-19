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
    public static void OnMultiplayerClick() { }

    public static string GetSteamNickname()
    {
        return "Player" + UnityEngine.Random.Range(1000, 9999);
    }
}
