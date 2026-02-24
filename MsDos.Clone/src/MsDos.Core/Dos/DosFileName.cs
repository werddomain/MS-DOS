using System.Text;

namespace MsDos.Core.Dos;

/// <summary>
/// Converts modern filenames to valid DOS 8.3 short names.
/// </summary>
public static class DosFileName
{
    /// <summary>Invalid characters for DOS filenames.</summary>
    private static readonly HashSet<char> InvalidChars = new("\"*+,/:;<=>?[\\]| \t".ToCharArray());

    /// <summary>
    /// Convert any filename to a valid DOS 8.3 short name (uppercase).
    /// Examples:
    ///   "my_document.txt"       → "MY_DOCUM.TXT"
    ///   "longfilename.backup"   → "LONGFILE.BAC"
    ///   "readme"                → "README"
    ///   ".hidden"               → "HIDDEN"
    ///   "hello world.doc"       → "HELLOWOR.DOC"
    ///   "file...name.c"         → "FILE___.C" (dots become underscores except last)
    /// </summary>
    /// <param name="filename">The modern filename to convert.</param>
    /// <returns>A valid DOS 8.3 filename.</returns>
    public static string ToShortName(string filename)
    {
        if (string.IsNullOrWhiteSpace(filename))
            return "NONAME";

        // Strip path components - just use the filename part
        int lastSep = filename.LastIndexOfAny(new[] { '/', '\\' });
        if (lastSep >= 0)
            filename = filename[(lastSep + 1)..];

        if (string.IsNullOrWhiteSpace(filename))
            return "NONAME";

        // Handle leading dot (hidden files) — strip it
        if (filename.StartsWith('.'))
            filename = filename.TrimStart('.');

        if (string.IsNullOrWhiteSpace(filename))
            return "NONAME";

        // Split on the LAST dot for extension
        string baseName, ext;
        int lastDot = filename.LastIndexOf('.');
        if (lastDot >= 0)
        {
            baseName = filename[..lastDot];
            ext = filename[(lastDot + 1)..];
        }
        else
        {
            baseName = filename;
            ext = "";
        }

        // Sanitize: replace invalid chars with underscore, uppercase
        baseName = Sanitize(baseName);
        ext = Sanitize(ext);

        // Truncate
        if (baseName.Length > 8)
            baseName = baseName[..8];
        if (ext.Length > 3)
            ext = ext[..3];

        // Ensure baseName is not empty
        if (string.IsNullOrEmpty(baseName))
            baseName = "NONAME";

        return string.IsNullOrEmpty(ext) ? baseName : $"{baseName}.{ext}";
    }

    /// <summary>
    /// Sanitize a string for DOS filename use: uppercase, replace invalid chars.
    /// </summary>
    private static string Sanitize(string input)
    {
        var sb = new StringBuilder(input.Length);
        foreach (char c in input.ToUpperInvariant())
        {
            if (c < 0x20 || c > 0x7E || InvalidChars.Contains(c))
                sb.Append('_');
            else
                sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Build a padded 11-byte DOS directory entry name (8+3, space-padded).
    /// </summary>
    public static string To11ByteName(string filename)
    {
        string shortName = ToShortName(filename);
        int dot = shortName.IndexOf('.');
        string basePart, extPart;
        if (dot >= 0)
        {
            basePart = shortName[..dot];
            extPart = shortName[(dot + 1)..];
        }
        else
        {
            basePart = shortName;
            extPart = "";
        }

        return basePart.PadRight(8) + extPart.PadRight(3);
    }
}
