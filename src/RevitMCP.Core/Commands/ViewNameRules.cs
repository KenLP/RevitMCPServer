using System;
using System.Linq;
using Autodesk.Revit.DB;

namespace RevitMCPAddin.Commands;

/// <summary>
/// Applies a caller-supplied name to a view and refuses to report success unless that exact name
/// landed. Revit rejects a view name it does not accept by throwing from the <c>Name</c> setter;
/// the old <c>try { view.Name = name; } catch { }</c> pattern swallowed that, so the view kept
/// Revit's placeholder ("Door Schedule 3") and the command still returned ok. A caller that relies
/// on its naming convention — and on finding the view again by that name — had no way to tell.
///
/// Revit stays the judge of what is acceptable: the name is always attempted, and the character
/// list below only labels a rejection, it never decides one. So a character Revit accepts is never
/// refused here, and a rule this list does not know about still surfaces as an error.
/// </summary>
internal static class ViewNameRules
{
    /// <summary>
    /// Characters Revit refuses in a view name — measured one by one on Revit 2027 (v0.8.36). Not
    /// the family-name set in <c>rename_element</c>: a view name may contain <c>*</c>, and may not
    /// contain a backtick.
    /// </summary>
    internal static readonly char[] IllegalChars = @"\:{}[]|;<>?~`".ToCharArray();

    /// <summary>The illegal characters present in <paramref name="name"/>, in first-seen order.</summary>
    internal static char[] IllegalCharsIn(string name) =>
        name.Where(ch => Array.IndexOf(IllegalChars, ch) >= 0).Distinct().ToArray();

    /// <summary>
    /// Sets <paramref name="view"/>.Name to <paramref name="requested"/> or throws a
    /// <see cref="RevitCommandException"/>: <c>invalid_chars</c> (400), <c>name_collision</c> (409),
    /// or <c>invalid_parameter</c> (400) for anything else Revit refused. Throwing inside the
    /// dispatcher's transaction rolls back the whole command, so a create command leaves nothing
    /// behind under a placeholder name.
    /// </summary>
    internal static void Apply(View view, string requested, string paramName = "name")
    {
        try
        {
            view.Name = requested;
        }
        catch (Autodesk.Revit.Exceptions.ArgumentException ex)
        {
            throw Classify(view, requested, paramName, ex.Message);
        }

        // Belt and braces: a setter that returns without throwing must still have applied it.
        if (!string.Equals(view.Name, requested, StringComparison.Ordinal))
            throw new RevitCommandException("invalid_parameter",
                $"Revit kept the name '{view.Name}' instead of the requested '{requested}'.");
    }

    private static RevitCommandException Classify(
        View view, string requested, string paramName, string revitMessage)
    {
        var bad = IllegalCharsIn(requested);
        if (bad.Length > 0)
            return new RevitCommandException("invalid_chars",
                $"Parameter '{paramName}' contains character(s) Revit does not allow in a view name: " +
                string.Join(" ", bad) + ". Revit disallows: " + string.Join(" ", IllegalChars) + ".");

        var clash = new FilteredElementCollector(view.Document)
            .OfClass(typeof(View))
            .Cast<View>()
            .FirstOrDefault(v => v.Id != view.Id && !v.IsTemplate && v.ViewType == view.ViewType &&
                                 string.Equals(v.Name, requested, StringComparison.OrdinalIgnoreCase));
        if (clash is not null)
            return new RevitCommandException("name_collision",
                $"A {view.ViewType} view named '{clash.Name}' already exists (id {clash.Id.Value}).");

        return new RevitCommandException("invalid_parameter",
            $"Revit rejected the name '{requested}': {revitMessage}");
    }
}
