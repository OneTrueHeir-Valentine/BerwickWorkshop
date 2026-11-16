using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Berwick.Core;

/// <summary>
/// Reads an event script back from the text the Events page shows (<see cref="ScriptText.Format"/>): four spaces per level,
/// the event line, assignments, if, else and while with the lines under them, calls on one line or spread over several (with
/// { } around a part that holds lines), raw data as Name [hex], and expressions written with parentheses around every
/// operator. Every event on both discs reads back to its original bytes.
/// </summary>
public static partial class ScriptCompiler
{
    private sealed record SourceLine(int Number, int Depth, string Text);

    public static IReadOnlyList<ScriptNode> Compile(string text, IReadOnlyList<string>? names)
    {
        var lines = new List<SourceLine>();
        var raw = text.Replace("\r\n", "\n").Split('\n');
        for (int i = 0; i < raw.Length; i++)
        {
            string line = raw[i].Replace("\t", "    ").TrimEnd();
            if (line.Length == 0) continue;
            int spaces = line.Length - line.TrimStart(' ').Length;
            if (spaces % 4 != 0) throw new EditException($"Line {i + 1}: indent lines by four spaces for each level.");
            lines.Add(new(i + 1, spaces / 4, line.Trim()));
        }
        var parser = new Parser(lines, CommandMap(names));
        var result = parser.Block(0);
        parser.End();
        return result;
    }

    /// <summary>Command numbers by the names the text uses: the game's names, the script's own words, and commandN.</summary>
    private static Dictionary<string, int> CommandMap(IReadOnlyList<string>? names)
    {
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int c = 0; c < (names?.Count ?? 0); c++)
        {
            string name = ScriptText.Name(c, names);
            if (!name.StartsWith("command", StringComparison.Ordinal)) map.TryAdd(name, c);
        }
        foreach (var (command, name) in ScriptText.Names) map[name] = command;
        return map;
    }

    [GeneratedRegex(@"^event \((\d+) local values?\)$")]
    private static partial Regex EventLine();
    [GeneratedRegex(@"^v(-?\d+) =(?: (.+))?$")]
    private static partial Regex AssignLine();
    [GeneratedRegex(@"^([A-Za-z_][A-Za-z0-9_]*|run event|wait|break) \[([0-9A-F]*)\]$")]
    private static partial Regex DataLine();
    [GeneratedRegex(@"^([A-Za-z_][A-Za-z0-9_]*|run event|wait|break)\($")]
    private static partial Regex OpenCall();

    private sealed class Parser(List<SourceLine> lines, Dictionary<string, int> commands)
    {
        private int at;

        private SourceLine? Peek => at < lines.Count ? lines[at] : null;

        public void End()
        {
            if (Peek is { } extra) throw new EditException($"Line {extra.Number}: this line is not part of the event.");
        }

        public List<ScriptNode> Block(int depth)
        {
            var list = new List<ScriptNode>();
            while (Peek is { } line && line.Depth >= depth)
            {
                if (line.Depth > depth) throw new EditException($"Line {line.Number}: this line is indented further than the line above allows.");
                if (line.Text is "else" or ")" or "}" or "},") break;
                list.Add(Statement(depth));
            }
            return list;
        }

        private ScriptNode Statement(int depth)
        {
            var line = lines[at++];
            string text = line.Text;
            if (EventLine().Match(text) is { Success: true } ev)
                return new ScriptNode { Command = ScriptParser.Event, Locals = Number(ev.Groups[1].Value, line), Args = [Block(depth + 1)] };
            if (AssignLine().Match(text) is { Success: true } assign)
            {
                int variable = Number(assign.Groups[1].Value, line);
                var value = assign.Groups[2].Success ? Inline(assign.Groups[2].Value, line) : Block(depth + 1);
                return new ScriptNode { Command = ScriptParser.Assign, Variable = variable, Args = [value] };
            }
            if (text.StartsWith("if ", StringComparison.Ordinal) || text.StartsWith("while ", StringComparison.Ordinal))
            {
                bool isIf = text[0] == 'i';
                string condition = text[(isIf ? 3 : 6)..];
                var test = condition == "(condition)" ? Block(depth + 2) : Inline(condition, line);
                var body = Block(depth + 1);
                if (!isIf) return new ScriptNode { Command = ScriptParser.While, Args = [test, body] };
                if (Peek is { Text: "else" } other && other.Depth == depth)
                {
                    at++;
                    return new ScriptNode { Command = ScriptParser.If, Args = [test, body, Block(depth + 1)] };
                }
                return new ScriptNode { Command = ScriptParser.If, Args = [test, body] };
            }
            if (text == "else") throw new EditException($"Line {line.Number}: else must follow the lines under an if.");
            if (DataLine().Match(text) is { Success: true } data)
            {
                byte[] bytes;
                try { bytes = Convert.FromHexString(data.Groups[2].Value); }
                catch (FormatException) { throw new EditException($"Line {line.Number}: the data in brackets must be pairs of hexadecimal digits."); }
                return new ScriptNode { Command = Command(data.Groups[1].Value, line), Data = bytes };
            }
            if (OpenCall().Match(text) is { Success: true } call)
            {
                int command = Command(call.Groups[1].Value, line);
                var args = new List<IReadOnlyList<ScriptNode>>();
                while (true)
                {
                    if (Peek is not { } next) throw new EditException($"Line {line.Number}: the arguments of this command are not closed with a line holding ).");
                    if (next.Depth == depth && next.Text == ")") { at++; break; }
                    if (next.Depth != depth + 1) throw new EditException($"Line {next.Number}: each argument goes on its own line, one level in.");
                    at++;
                    if (next.Text == "{")
                    {
                        args.Add(Block(depth + 2));
                        if (Peek is not { } close || close.Depth != depth + 1 || close.Text is not ("}" or "},"))
                            throw new EditException($"Line {next.Number}: the lines opened with {{ are not closed with }}.");
                        at++;
                    }
                    else args.Add(Inline(next.Text.EndsWith(',') ? next.Text[..^1] : next.Text, next));
                }
                return new ScriptNode { Command = command, Args = args };
            }
            var expression = Inline(text, line);
            if (expression.Count != 1) throw new EditException($"Line {line.Number}: \"nothing\" is only used as an argument.");
            return expression[0];
        }

        private int Command(string name, SourceLine line)
        {
            if (commands.TryGetValue(name, out int command)) return command;
            if (name.StartsWith("command", StringComparison.Ordinal) && int.TryParse(name.AsSpan(7), NumberStyles.None, CultureInfo.InvariantCulture, out int number) && number >= 0) return number;
            throw new EditException($"Line {line.Number}: there is no command named {name}.");
        }

        private static int Number(string text, SourceLine line) =>
            int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int value) ? value : throw new EditException($"Line {line.Number}: {text} is not a whole number the game can hold.");

        // One value as a block: "nothing" is an empty block, anything else a block of one command.
        private List<ScriptNode> Inline(string text, SourceLine line)
        {
            var reader = new ExpressionReader(text, line, this);
            var block = reader.Value();
            reader.SkipSpaces();
            if (!reader.AtEnd) throw new EditException($"Line {line.Number}: unexpected text after {text[..reader.Position].Trim()}.");
            return block;
        }

        private sealed class ExpressionReader(string text, SourceLine line, Parser parser)
        {
            public int Position;
            public bool AtEnd => Position >= text.Length;
            private char Current => Position < text.Length ? text[Position] : '\0';
            public void SkipSpaces() { while (Position < text.Length && text[Position] == ' ') Position++; }
            private EditException Error(string message) => new($"Line {line.Number}: {message}");
            private bool Next(string word)
            {
                if (string.CompareOrdinal(text, Position, word, 0, word.Length) != 0) return false;
                Position += word.Length;
                return true;
            }

            private static readonly (string Text, int Command)[] Operators =
                ScriptText.Operators.Select(o => (o.Value, o.Key)).OrderByDescending(o => o.Value.Length).ToArray();

            public List<ScriptNode> Value()
            {
                SkipSpaces();
                if (AtEnd) throw Error("a value is missing.");
                char c = Current;
                if (c == '"') return [new ScriptNode { Command = ScriptParser.TextValue, Text = Text() }];
                if (c == '(')
                {
                    Position++;
                    var left = Value();
                    SkipSpaces();
                    if (Current == ')') { Position++; return left; }
                    var op = Operators.FirstOrDefault(o => Next(o.Text + " "));
                    if (op.Text is null) throw Error("expected an operator such as ==, + or and inside the parentheses.");
                    var right = Value();
                    SkipSpaces();
                    if (Current != ')') throw Error("a closing parenthesis is missing.");
                    Position++;
                    return [new ScriptNode { Command = op.Command, Args = [left, right] }];
                }
                if (c == '-' && Position + 1 < text.Length && char.IsAsciiDigit(text[Position + 1])) return [Literal()];
                if (c == '-')
                {
                    Position++;
                    return [new ScriptNode { Command = 0x15, Args = [Value()] }];
                }
                if (char.IsAsciiDigit(c)) return [Literal()];
                if (Next("not ")) return [new ScriptNode { Command = 0x16, Args = [Value()] }];
                if (Next("run event ")) return [new ScriptNode { Command = ScriptParser.RunEvent, Args = [Value()] }];
                if (Next("wait "))
                {
                    var frames = Value();
                    SkipSpaces();
                    if (!Next("frames") && !Next("frame")) throw Error("write wait as wait 30 frames.");
                    return [new ScriptNode { Command = ScriptParser.Wait, Args = [frames] }];
                }
                if (c == 'v' && Position + 1 < text.Length && (char.IsAsciiDigit(text[Position + 1]) || text[Position + 1] == '-'))
                {
                    int start = ++Position;
                    if (Current == '-') Position++;
                    while (char.IsAsciiDigit(Current)) Position++;
                    return [new ScriptNode { Command = ScriptParser.Variable, Variable = Number(text[start..Position], line) }];
                }
                // A call (break on its own has no parentheses), or "nothing" for an empty argument.
                string name;
                if (Next("run event(")) { name = "run event"; Position--; }
                else
                {
                    int start = Position;
                    while (char.IsAsciiLetterOrDigit(Current) || Current == '_') Position++;
                    if (start == Position) throw Error($"unexpected character {c}.");
                    name = text[start..Position];
                }
                if (Current != '(')
                {
                    if (name == "break") return [new ScriptNode { Command = ScriptParser.Break }];
                    if (name == "nothing") return [];
                    throw Error($"{name} needs its arguments in parentheses.");
                }
                Position++;
                int command = parser.Command(name, line);
                var args = new List<IReadOnlyList<ScriptNode>>();
                SkipSpaces();
                if (Current == ')') { Position++; return [new ScriptNode { Command = command, Args = args }]; }
                while (true)
                {
                    args.Add(Value());
                    SkipSpaces();
                    if (Current == ')') { Position++; break; }
                    if (Current != ',') throw Error("separate the arguments with commas and close them with ).");
                    Position++;
                }
                return [new ScriptNode { Command = command, Args = args }];
            }

            private ScriptNode Literal()
            {
                int start = Position;
                if (Current == '-') Position++;
                while (char.IsAsciiDigit(Current)) Position++;
                return new ScriptNode { Command = ScriptParser.Number, Value = Number(text[start..Position], line) };
            }

            private string Text()
            {
                var sb = new StringBuilder();
                Position++;
                while (true)
                {
                    if (AtEnd) throw Error("a text is missing its closing quote.");
                    char c = text[Position++];
                    if (c == '"') return sb.ToString();
                    if (c != '\\') { sb.Append(c); continue; }
                    if (AtEnd) throw Error("a text ends with a backslash.");
                    char e = text[Position++];
                    sb.Append(e switch { '\\' => '\\', '"' => '"', 'r' => '\r', 'n' => '\n', 't' => '\t', _ => throw Error($"\\{e} is not a known escape; use \\\\, \\\", \\r, \\n or \\t.") });
                }
            }
        }
    }
}
