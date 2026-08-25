using System;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Perianth.Formats.Diagnostics;
using Perianth.Formats.Io;

namespace Perianth.Formats.Juice;

/// <summary>
/// One declaration in the game's own text configuration language — an
/// <c>.mitem</c>, a <c>.mvendorconfig</c>, or any other <c>juice</c> file.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is a span index, not a parse.</b> It records where the declaration
/// and each field's value sit in the original bytes and nothing about what they
/// mean, so an edit is a splice and every byte outside it survives untouched.
/// That is the same rule the three binary writers keep — a writer has no
/// opinions — expressed in the only way that makes sense for text: the file is
/// never reassembled from a model of itself.
/// </para>
/// <para>
/// The alternative was a real parser over the game's schema, which is 268
/// <c>.fruit</c> files, 263 enums and 887 classes. Modelling that is the failure
/// this project exists to avoid, and it would buy nothing: an authoring tool
/// copies a shipped declaration and changes a handful of fields, so every line
/// it does not understand is a line it must reproduce exactly rather than
/// interpret. Roadmap §10.88 and §10.89.
/// </para>
/// <para>
/// A field's value is either the rest of its line, or a brace block beginning on
/// the following line. Both are recorded as ranges; a block is never decoded,
/// because nothing here needs to look inside one and a reader that did would be
/// the parser this type exists not to be.
/// </para>
/// </remarks>
public sealed class JuiceDocument
{
    private readonly ReadOnlyMemory<byte> _bytes;

    private JuiceDocument(
        string path,
        ReadOnlyMemory<byte> bytes,
        string declaredClass,
        ByteRange name,
        ByteRange uid,
        ByteRange declaration,
        ImmutableArray<JuiceField> fields)
    {
        Path = path;
        _bytes = bytes;
        DeclaredClass = declaredClass;
        NameRange = name;
        UidRange = uid;
        DeclarationRange = declaration;
        Fields = fields;
    }

    /// <summary>The path as the caller supplied it.</summary>
    public string Path { get; }

    /// <summary>The declared class — <c>CostumeItemStreetHairLow</c> and its like.</summary>
    /// <remarks>
    /// For an item this <b>is the slot</b>: 26 classes account for all 3,038
    /// shipped items and the costume ones name their slot outright, so choosing a
    /// slot means choosing which declaration to copy rather than setting a field
    /// (Roadmap §10.89).
    /// </remarks>
    public string DeclaredClass { get; }

    /// <summary>Where the declared name sits, quotes included if it had them.</summary>
    public ByteRange NameRange { get; }

    /// <summary>Where the 32 hex digits of the uid sit.</summary>
    public ByteRange UidRange { get; }

    /// <summary>Whether the declaration states a uid at all.</summary>
    /// <remarks>
    /// Many do not. A recipe and a starting-inventory setting are both declared
    /// without one, and are referred to by name rather than by uid.
    /// </remarks>
    public bool HasUid => UidRange.Length == UidDigits;

    /// <summary>
    /// The whole declaration, from the class token to the newline after its
    /// closing brace.
    /// </summary>
    /// <remarks>
    /// What a copy is taken from. Some things the game declares are whole
    /// declarations rather than entries in a list — a recipe is one — so making
    /// a new one means copying an existing declaration entire and changing the
    /// few fields that distinguish it. That is the same move as copying an
    /// item's file, at a smaller scale, and it is what keeps a declaration's
    /// unmentioned lines exactly as they were.
    /// </remarks>
    public ByteRange DeclarationRange { get; }

    /// <summary>The fields of the declaration's body, in file order.</summary>
    public ImmutableArray<JuiceField> Fields { get; }

    /// <summary>The bytes as they stand, edits included.</summary>
    public ReadOnlyMemory<byte> Bytes => _bytes;

    /// <summary>Reads the first declaration of a juice file and indexes its fields.</summary>
    /// <remarks>
    /// The <em>first</em> declaration, deliberately. A costume parent file
    /// <c>include</c>s its variants and then declares one thing; a vendor config
    /// declares forty. Indexing one and saying so is a weaker claim than parsing
    /// the file, and it is the one this type can keep.
    /// </remarks>
    public static Result<JuiceDocument> Read(SourceFile file) => Read(file, null);

    /// <summary>Reads the declaration of a given name, or the first when none is named.</summary>
    /// <remarks>
    /// One file often declares many things — the base game's vendor config holds
    /// forty shops in 59 KB — so an operation that meant to edit one of them has
    /// to say which. Naming it is the whole guard: without it, "add this to the
    /// shop" would silently mean "add it to whichever shop happens to be first".
    /// </remarks>
    public static Result<JuiceDocument> Read(SourceFile file, string? declaredName)
    {
        ArgumentNullException.ThrowIfNull(file);

        // Decoded one byte to one char rather than as UTF-8, which is what makes
        // the ranges byte offsets and not character offsets. It also cannot fail:
        // one shipped item is Windows-1252 (a curly apostrophe in "kids' toy"),
        // and refusing it would block a real file for a reason that does not
        // matter here. Every character this looks for — braces, quotes, '<',
        // '=', whitespace and "my" — is ASCII, and no byte of a multi-byte UTF-8
        // sequence is ever an ASCII byte, so the structure is found correctly
        // whatever the file's encoding turns out to be. Nothing here interprets
        // text beyond that, and an edit splices bytes.
        string text = Latin1.GetString(file.Bytes);

        // The declaration is "<Class> <Name> < uid=HEX >" at the start of a line,
        // with the name optionally quoted because it may contain spaces
        // ("10-sided Dice"). Requiring an unquoted name silently mis-read a
        // majority of shipped items once already — Roadmap §10.89.
        int at = 0;
        while (at < text.Length)
        {
            int lineEnd = text.IndexOf('\n', at);
            if (lineEnd < 0) { lineEnd = text.Length; }

            Result<JuiceDocument>? parsed = TryDeclaration(file.Path, file.Bytes, text, at, lineEnd, declaredName is not null);
            if (parsed is not null)
            {
                if (declaredName is null || !parsed.Value.IsSuccess)
                {
                    return parsed.Value;
                }

                string found = Unquoted(parsed.Value.Value, parsed.Value.Value.NameRange);
                if (found.Equals(declaredName, StringComparison.Ordinal))
                {
                    return parsed.Value;
                }
            }

            at = lineEnd + 1;
        }

        return declaredName is null
            ? Refusal.Malformed("The file holds no declaration of the form 'Class Name < uid=… >'.")
            : Refusal.Unsupported(string.Create(
                CultureInfo.InvariantCulture,
                $"The file declares nothing named '{declaredName}'."));
    }

    /// <summary>Finds a field by name, or fails when the declaration has none.</summary>
    public bool TryGetField(string name, out JuiceField field)
    {
        ArgumentNullException.ThrowIfNull(name);

        foreach (JuiceField candidate in Fields)
        {
            if (candidate.Name.Equals(name, StringComparison.Ordinal))
            {
                field = candidate;
                return true;
            }
        }

        field = default;
        return false;
    }

    /// <summary>The same document with one field's value replaced.</summary>
    /// <remarks>
    /// Refuses a field the declaration does not carry rather than appending one.
    /// The files are sparse — a field is written only when set — so an absent
    /// field is not an error in the data, but silently inventing one is a
    /// different operation from editing, and a caller that meant to add should
    /// say so. Refuses a block field for the same reason: replacing a brace block
    /// with a line would produce a file that still parses and means something
    /// else.
    /// </remarks>
    public Result<JuiceDocument> WithField(string name, string value)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(value);

        if (!TryGetField(name, out JuiceField field))
        {
            return Refusal.Unsupported(string.Create(
                CultureInfo.InvariantCulture,
                $"'{DeclaredClass}' carries no field '{name}', and editing does not add one."));
        }

        if (field.IsBlock)
        {
            return Refusal.Unsupported(string.Create(
                CultureInfo.InvariantCulture,
                $"'{name}' carries a brace block, which this cannot replace with a single value."));
        }

        return Splice(field.Value, value);
    }

    /// <summary>The declared name, without the quotes it may be written with.</summary>
    public string DeclaredName => Unquoted(this, NameRange);

    /// <summary>The same document with one more entry at the end of a block field.</summary>
    /// <remarks>
    /// <para>
    /// Inserted immediately before the block's own closing brace, so the entries
    /// already there are untouched and the file's order is the order things were
    /// added. The caller supplies the entry's text including its indentation,
    /// because the shape of an entry belongs to whatever the block holds — a
    /// vendor's item, a loot entry, a starting-inventory setting — and this type
    /// deliberately knows none of them.
    /// </para>
    /// <para>
    /// Refuses a field that is not a block, which is the mirror of
    /// <see cref="WithField"/> refusing one that is: appending a line to a
    /// single-value field would produce a file that still parses and means
    /// something else.
    /// </para>
    /// </remarks>
    public Result<JuiceDocument> WithBlockEntry(string name, string entry)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(entry);

        if (!TryGetField(name, out JuiceField field))
        {
            return Refusal.Unsupported(string.Create(
                CultureInfo.InvariantCulture,
                $"'{DeclaredClass}' carries no field '{name}'."));
        }

        if (!field.IsBlock)
        {
            return Refusal.Unsupported(string.Create(
                CultureInfo.InvariantCulture,
                $"'{name}' is a single value, not a block, so it holds no entries."));
        }

        return Splice(new ByteRange(field.BlockEnd, 0), entry);
    }

    /// <summary>The same document with a block field's entries replaced wholesale.</summary>
    /// <remarks>
    /// The counterpart to <see cref="WithBlockEntry"/>, for the case where the
    /// entries a copied declaration came with are the wrong ones rather than the
    /// first ones — a recipe copied for its shape lists the ingredients of the
    /// thing it used to make. Everything outside the braces survives, including
    /// the field's own line and the closing brace, so this stays a splice.
    /// </remarks>
    public Result<JuiceDocument> WithBlockContents(string name, string contents)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(contents);

        if (!TryGetField(name, out JuiceField field))
        {
            return Refusal.Unsupported(string.Create(
                CultureInfo.InvariantCulture,
                $"'{DeclaredClass}' carries no field '{name}'."));
        }

        if (!field.IsBlock)
        {
            return Refusal.Unsupported(string.Create(
                CultureInfo.InvariantCulture,
                $"'{name}' is a single value, not a block, so it holds no entries."));
        }

        return Splice(new ByteRange(field.BlockStart, field.BlockEnd - field.BlockStart), contents);
    }

    /// <summary>The same document under a new name, keeping whatever uid it had.</summary>
    public Result<JuiceDocument> WithName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        return name.Length == 0
            ? Refusal.Unsupported("A declaration's name cannot be empty.")
            : Splice(NameRange, Quoted(name));
    }

    /// <summary>The same document under a new name and uid.</summary>
    public Result<JuiceDocument> WithDeclaration(string name, string uid)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(uid);

        // Refused rather than inserted. A uid-less declaration's uid range is
        // empty and sits at offset zero, so splicing into it would write the
        // digits over the head of the file — a corruption that still parses.
        if (!HasUid)
        {
            return Refusal.Unsupported(string.Create(
                CultureInfo.InvariantCulture,
                $"'{DeclaredClass} {DeclaredName}' states no uid, so there is none to change. Rename it instead."));
        }

        if (!IsUid(uid))
        {
            return Refusal.Unsupported(string.Create(
                CultureInfo.InvariantCulture,
                $"A uid is 32 upper-case hex digits and '{uid}' is not."));
        }

        if (name.Length == 0)
        {
            return Refusal.Unsupported("A declaration's name cannot be empty.");
        }

        // The uid sits after the name in the file, so replacing the name first
        // would move it. Later range first, and the two never overlap.
        Result<JuiceDocument> withUid = Splice(UidRange, uid);
        return withUid.IsSuccess
            ? withUid.Value.Splice(NameRange, Quoted(name))
            : withUid;
    }

    /// <summary>Whether a string is a uid as these files spell one.</summary>
    /// <remarks>
    /// 3,532 uids across the shipped items are all exactly 32 upper-case hex
    /// digits, with no separators (Roadmap §10.92).
    /// </remarks>
    public static bool IsUid(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (value.Length != UidDigits)
        {
            return false;
        }

        foreach (char c in value)
        {
            if (c is not ((>= '0' and <= '9') or (>= 'A' and <= 'F')))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>How many hex digits a uid has.</summary>
    public const int UidDigits = 32;

    /// <summary>
    /// A byte-per-char decoding, so a range is a byte offset.
    /// </summary>
    private static Encoding Latin1 => Encoding.Latin1;

    private static string Quoted(string name)
    {
        foreach (char c in name)
        {
            if (c is ' ' or '\t')
            {
                return "\"" + name + "\"";
            }
        }

        return name;
    }

    private Result<JuiceDocument> Splice(ByteRange range, string replacement)
    {
        byte[] insert = Encoding.UTF8.GetBytes(replacement);
        byte[] built = new byte[_bytes.Length - range.Length + insert.Length];

        _bytes.Span[..range.Offset].CopyTo(built);
        insert.CopyTo(built, range.Offset);
        _bytes.Span[(range.Offset + range.Length)..]
            .CopyTo(built.AsSpan(range.Offset + insert.Length));

        // Re-reading rather than shifting the ranges by hand. The spans are the
        // whole correctness of this type, and a splice that adjusted them would
        // have to be right about every one; re-reading is right by construction
        // and these files are kilobytes.
        return Read(SourceFile.FromMemory(Path, built));
    }

    private static Result<JuiceDocument>? TryDeclaration(
        string path, ReadOnlySpan<byte> bytes, string text, int lineStart, int lineEnd, bool nested)
    {
        ReadOnlySpan<char> whole = text.AsSpan(lineStart, lineEnd - lineStart);
        if (whole.Length > 0 && whole[^1] == '\r')
        {
            whole = whole[..^1];
        }

        // A nested declaration is indented, and only a search by name may match
        // one. Left to itself, "the first declaration" must stay the file's own
        // top-level one, or reading an item would start returning whatever
        // happened to be nested inside it.
        int indent = 0;
        while (indent < whole.Length && whole[indent] is ' ' or '\t')
        {
            indent++;
        }

        if (indent > 0 && !nested)
        {
            return null;
        }

        ReadOnlySpan<char> line = whole[indent..];
        if (line.Length == 0 || line[0] is '/' or '#')
        {
            return null;
        }

        // The uid is optional. `starting_inventory.juice` carries none at all,
        // on any of its 57 settings, so requiring one would put the route that
        // covers two-thirds of costume entries out of reach (Roadmap §10.92).
        int uidAt = line.IndexOf("uid=", StringComparison.Ordinal);

        int classEnd = line.IndexOf(' ');
        if (classEnd <= 0)
        {
            return null;
        }

        string declaredClass = new(line[..classEnd]);
        int nameStart = classEnd + 1;
        while (nameStart < line.Length && line[nameStart] == ' ')
        {
            nameStart++;
        }

        int nameEnd;
        if (nameStart < line.Length && line[nameStart] == '"')
        {
            int close = line[(nameStart + 1)..].IndexOf('"');
            if (close < 0)
            {
                return null;
            }

            nameEnd = nameStart + close + 2;
        }
        else
        {
            int space = line[nameStart..].IndexOf(' ');
            nameEnd = space < 0 ? line.Length : nameStart + space;
        }

        if (nameEnd <= nameStart)
        {
            return null;
        }

        int digitsAt = 0;
        if (uidAt >= 0)
        {
            digitsAt = uidAt + "uid=".Length;
            int digitsEnd = digitsAt;
            while (digitsEnd < line.Length && Uri.IsHexDigit(line[digitsEnd]))
            {
                digitsEnd++;
            }

            if (digitsEnd - digitsAt != UidDigits)
            {
                return null;
            }
        }

        // A declaration's body opens on the next line. Requiring it is what
        // keeps an ordinary field line — or a vendor's "VendorItem 3" — from
        // being read as a declaration of its own.
        int nextLine = lineEnd + 1;
        int bodyLineEnd = text.IndexOf('\n', nextLine);
        if (bodyLineEnd < 0)
        {
            bodyLineEnd = text.Length;
        }

        if (!text.AsSpan(nextLine, bodyLineEnd - nextLine).Trim().SequenceEqual("{"))
        {
            return null;
        }

        int bodyStart = text.IndexOf('{', lineEnd);
        if (bodyStart < 0)
        {
            return Refusal.Malformed(string.Create(
                CultureInfo.InvariantCulture,
                $"The declaration of '{declaredClass}' is not followed by a body."));
        }

        ImmutableArray<JuiceField> fields = ReadFields(text, bodyStart + 1);

        // Through the newline after the closing brace, so a copied declaration
        // is a whole one and appending it needs no separator invented.
        int closeLine = BlockEndOf(text, lineEnd);
        int closeEnd = text.IndexOf('\n', closeLine);
        closeEnd = closeEnd < 0 ? text.Length : closeEnd + 1;

        return Result.Ok(new JuiceDocument(
            path,
            bytes.ToArray(),
            declaredClass,
            new ByteRange(lineStart + indent + nameStart, nameEnd - nameStart),
            uidAt >= 0
                ? new ByteRange(lineStart + indent + digitsAt, UidDigits)
                : default,
            new ByteRange(lineStart, closeEnd - lineStart),
            fields));
    }

    /// <summary>
    /// Where a block's closing brace line starts, given the end of the line that
    /// named it.
    /// </summary>
    /// <remarks>
    /// Counts braces rather than matching indentation, because a block's entries
    /// nest — a vendor's item list holds items that each open a brace of their
    /// own — and stopping at the first closing line would insert inside the last
    /// entry rather than after it.
    /// </remarks>
    private static int BlockEndOf(string text, int lineEnd)
    {
        int depth = 0;
        int lineStart = lineEnd + 1;
        bool inString = false;
        bool escaped = false;

        for (int at = lineEnd; at < text.Length; at++)
        {
            char c = text[at];
            if (escaped)
            {
                escaped = false;
                continue;
            }

            if (c == '\\')
            {
                escaped = true;
            }
            else if (c == '"')
            {
                inString = !inString;
            }
            else if (c == '\n')
            {
                lineStart = at + 1;
                inString = false;
            }
            else if (!inString && c == '{')
            {
                depth++;
            }
            else if (!inString && c == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return lineStart;
                }
            }
        }

        return text.Length;
    }

    /// <summary>
    /// Where a block's entries begin — after the newline that ends its opening
    /// brace line, so replacing them leaves that line alone.
    /// </summary>
    private static int BlockContentsFrom(string text, int lineEnd)
    {
        int open = text.IndexOf('{', lineEnd);
        if (open < 0)
        {
            return text.Length;
        }

        int newline = text.IndexOf('\n', open);
        return newline < 0 ? text.Length : newline + 1;
    }

    /// <summary>
    /// How far a line moves the brace depth, ignoring braces inside quoted text.
    /// </summary>
    /// <remarks>
    /// Not a nicety. A vendor's <c>myUIName</c> ends
    /// <c>… text = \"Freeman's Tacos\"}"</c> — a literal closing brace inside the
    /// localisation blob — so counting braces blindly drives the depth to zero
    /// and stops indexing after the first field. The declaration still reads and
    /// still writes back byte-for-byte, which is why the corpus oracle cannot see
    /// this: it proves the ranges agree, not that every field was found.
    /// </remarks>
    private static int BraceBalance(ReadOnlySpan<char> line)
    {
        int balance = 0;
        bool inString = false;
        bool escaped = false;

        foreach (char c in line)
        {
            if (escaped)
            {
                escaped = false;
                continue;
            }

            if (c == '\\')
            {
                escaped = true;
            }
            else if (c == '"')
            {
                inString = !inString;
            }
            else if (!inString && c == '{')
            {
                balance++;
            }
            else if (!inString && c == '}')
            {
                balance--;
            }
        }

        return balance;
    }

    private static string Unquoted(JuiceDocument document, ByteRange range)
    {
        string text = Latin1.GetString(
            document._bytes.Span.Slice(range.Offset, range.Length));
        return text.Length >= 2 && text[0] == '"' && text[^1] == '"'
            ? text[1..^1]
            : text;
    }

    private static ImmutableArray<JuiceField> ReadFields(string text, int at)
    {
        ImmutableArray<JuiceField>.Builder fields = ImmutableArray.CreateBuilder<JuiceField>();
        int depth = 1;

        while (at < text.Length && depth > 0)
        {
            int lineEnd = text.IndexOf('\n', at);
            if (lineEnd < 0) { lineEnd = text.Length; }

            ReadOnlySpan<char> line = text.AsSpan(at, lineEnd - at);
            ReadOnlySpan<char> trimmed = line.TrimStart();
            int indent = line.Length - trimmed.Length;

            // Only the declaration's own level is indexed. A field nested inside
            // a block belongs to that block, and hoisting it here would let a
            // caller edit "myItem" and hit an ingredient rather than the item.
            if (depth == 1 && trimmed.StartsWith("my", StringComparison.Ordinal))
            {
                int nameEnd = 0;
                while (nameEnd < trimmed.Length && !char.IsWhiteSpace(trimmed[nameEnd]))
                {
                    nameEnd++;
                }

                string name = new(trimmed[..nameEnd]);

                // The line's content ends before a CRLF's carriage return. Items
                // are LF and vendor configs are CRLF, so leaving it in made every
                // block field in a CRLF file look like an inline field whose
                // value was "\r" — which parses, and then appends an entry in
                // the wrong place.
                int contentEnd = lineEnd > at && text[lineEnd - 1] == '\r' ? lineEnd - 1 : lineEnd;

                int valueStart = at + indent + nameEnd;
                while (valueStart < contentEnd && (text[valueStart] == ' ' || text[valueStart] == '\t'))
                {
                    valueStart++;
                }

                bool isBlock = valueStart >= contentEnd;
                fields.Add(new JuiceField(
                    name,
                    new ByteRange(valueStart, isBlock ? 0 : contentEnd - valueStart),
                    isBlock,
                    isBlock ? BlockContentsFrom(text, lineEnd) : 0,
                    isBlock ? BlockEndOf(text, lineEnd) : 0));
            }

            depth += BraceBalance(line);

            at = lineEnd + 1;
        }

        return fields.ToImmutable();
    }
}

/// <summary>One field of a juice declaration, as a name and where its value sits.</summary>
/// <param name="Name">The field's name, <c>myModel</c> and its like.</param>
/// <param name="Value">Where the value sits, empty for a block field.</param>
/// <param name="IsBlock">Whether the value is a brace block on the following lines.</param>
/// <param name="BlockStart">
/// Where the block's entries begin, just after its opening brace line. Zero for
/// a field that is not a block.
/// </param>
/// <param name="BlockEnd">
/// Where the block's closing brace line begins, which is where another entry
/// goes. Zero for a field that is not a block.
/// </param>
public readonly record struct JuiceField(
    string Name, ByteRange Value, bool IsBlock, int BlockStart, int BlockEnd);
