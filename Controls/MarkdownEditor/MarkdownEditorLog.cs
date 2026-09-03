using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Evania.Controls.MarkdownEditor;

/// <summary>
/// Debug-only tracing for <see cref="MarkdownEditor"/>.
/// </summary>
/// <remarks>
/// The editor is written from scratch, so every layer - buffer, layout, caret, OS text input - can
/// fail independently and silently. Routing all of it through one channel makes the Output window a
/// readable transcript of what the control did, in order. Every member is marked
/// <see cref="ConditionalAttribute"/> so the calls and their argument evaluation disappear entirely
/// from Release builds.
/// </remarks>
internal static class MarkdownEditorLog
{
    private const string Category = "MarkdownEditor";

    /// <summary>Set to true to re-enable editor tracing during diagnosis.</summary>
    internal static bool IsEnabled { get; set; } = false;

    [Conditional("DEBUG")]
    internal static void Write(string message, [CallerMemberName] string caller = "")
    {
        if (!IsEnabled)
        {
            return;
        }

        Debug.WriteLine($"[{Category}] {caller}: {message}");
    }

    [Conditional("DEBUG")]
    internal static void Enter([CallerMemberName] string caller = "") => Write("enter", caller);

    [Conditional("DEBUG")]
    internal static void Exit(string reason, [CallerMemberName] string caller = "") => Write($"exit ({reason})", caller);

    [Conditional("DEBUG")]
    internal static void Error(Exception exception, [CallerMemberName] string caller = "")
        => Write($"EXCEPTION {exception.GetType().Name}: {exception.Message}", caller);

    /// <summary>Shortens and escapes user text so a paste of a whole note cannot flood the log.</summary>
    internal static string Snippet(string? text, int maxLength = 40)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "\"\"";
        }

        string escaped = text.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
        return escaped.Length <= maxLength
            ? $"\"{escaped}\""
            : $"\"{escaped[..maxLength]}\"(+{escaped.Length - maxLength})";
    }
}
