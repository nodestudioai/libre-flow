namespace LibreFlow.Core.Autocorrect;

public enum Casing { Lower, Capitalized, Other }

public static class TextUtil
{
    public static bool IsAllLetters(string s)
    {
        if (s.Length == 0) return false;
        foreach (var c in s) if (!char.IsLetter(c)) return false;
        return true;
    }

    public static char FoldChar(char c) => c switch
    {
        'á' or 'à' or 'ä' or 'â' => 'a',
        'é' or 'è' or 'ë' or 'ê' => 'e',
        'í' or 'ì' or 'ï' or 'î' => 'i',
        'ó' or 'ò' or 'ö' or 'ô' => 'o',
        'ú' or 'ù' or 'ü' or 'û' => 'u',
        _ => c,
    };

    /// <summary>Lower-case form without accents. Keeps ñ, which is a different letter.</summary>
    public static string Fold(string lower) => string.Create(lower.Length, lower, static (span, src) =>
    {
        for (var i = 0; i < src.Length; i++) span[i] = FoldChar(src[i]);
    });

    public static Casing GetCasing(string s)
    {
        var upper = 0;
        foreach (var c in s) if (char.IsUpper(c)) upper++;
        if (upper == 0) return Casing.Lower;
        if (upper == 1 && char.IsUpper(s[0])) return Casing.Capitalized;
        return Casing.Other;
    }

    public static string Capitalize(string s) =>
        s.Length == 0 || char.IsUpper(s[0]) ? s : char.ToUpperInvariant(s[0]) + s[1..];

    public static bool IsVowel(char c) => FoldChar(c) is 'a' or 'e' or 'i' or 'o' or 'u';

    public static int CountWords(string text)
    {
        var n = 0;
        var inWord = false;
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c)) inWord = false;
            else if (!inWord) { inWord = true; n++; }
        }
        return n;
    }
}
