namespace GrowTool77.Forms;

// "high" matches "High Five" and "Highway Sign" (name starts with it) and
// "Big High Heels" (a word starts with it).
public static class ListFilter
{
    public static bool Matches(string name, string text)
    {
        text = text.Trim();
        if (text.Length == 0) return true;
        if (name.StartsWith(text, StringComparison.OrdinalIgnoreCase)) return true;
        foreach (var word in name.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            if (word.StartsWith(text, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    public static string RarityText(int rarity) => rarity <= 0 || rarity >= 999 ? "—" : rarity.ToString();
}
