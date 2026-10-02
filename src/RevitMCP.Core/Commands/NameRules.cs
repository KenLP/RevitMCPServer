using System;
using System.Linq;
using Autodesk.Revit.DB;

namespace RevitMCPAddin.Commands;

/// <summary>
/// Applies a caller-supplied name (view, sheet number, group type, level, grid, room) and refuses to report
/// success unless that exact value landed. Revit rejects a value it does not accept by throwing from
/// the setter; the old <c>try { x.Name = name; } catch { }</c> pattern swallowed that, so the element
/// kept Revit's placeholder ("Door Schedule 3", "A102", "Group 4") and the command still returned ok.
/// A caller that relies on its naming convention — and on finding the element again by that name —
/// had no way to tell.
///
/// Revit stays the judge of what is acceptable: the value is always attempted, and the character
/// list below only labels a rejection, it never decides one. So a character Revit accepts is never
/// refused here, and a rule this list does not know about still surfaces as an error.
///
/// Every failure is thrown inside the dispatcher's transaction, so a create command that fails on
/// its name rolls back whole — nothing is left behind under a placeholder.
/// </summary>
internal static class NameRules
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
    /// Names a view (schedule, plan, section, 3D, sheet…). A clash is another non-template view of
    /// the same <see cref="ViewType"/>, which is how Revit scopes view-name uniqueness.
    /// </summary>
    internal static void ApplyViewName(View view, string requested, string paramName = "name") =>
        Apply(() => view.Name, v => view.Name = v, requested, paramName, "view name",
            () => new FilteredElementCollector(view.Document)
                .OfClass(typeof(View))
                .Cast<View>()
                .Where(v => v.Id != view.Id && !v.IsTemplate && v.ViewType == view.ViewType &&
                            string.Equals(v.Name, requested, StringComparison.OrdinalIgnoreCase))
                .Select(v => $"A {view.ViewType} view named '{v.Name}' already exists (id {v.Id.Value}).")
                .FirstOrDefault());

    /// <summary>Sets a sheet's number. Sheet numbers are unique across the document.</summary>
    internal static void ApplySheetNumber(ViewSheet sheet, string requested, string paramName = "sheetNumber") =>
        Apply(() => sheet.SheetNumber, v => sheet.SheetNumber = v, requested, paramName, "sheet number",
            () => new FilteredElementCollector(sheet.Document)
                .OfClass(typeof(ViewSheet))
                .Cast<ViewSheet>()
                .Where(s => s.Id != sheet.Id &&
                            string.Equals(s.SheetNumber, requested, StringComparison.OrdinalIgnoreCase))
                .Select(s => $"Sheet number '{s.SheetNumber}' is already used by '{s.Name}' (id {s.Id.Value}).")
                .FirstOrDefault());

    /// <summary>Names a group type. Group type names are unique across the document.</summary>
    internal static void ApplyGroupTypeName(GroupType type, string requested, string paramName = "name") =>
        Apply(() => type.Name, v => type.Name = v, requested, paramName, "group type name",
            () => new FilteredElementCollector(type.Document)
                .OfClass(typeof(GroupType))
                .Where(t => t.Id != type.Id &&
                            string.Equals(t.Name, requested, StringComparison.OrdinalIgnoreCase))
                .Select(t => $"A group type named '{t.Name}' already exists (id {t.Id.Value}).")
                .FirstOrDefault());

    /// <summary>Names a level. Level names are unique across the document.</summary>
    internal static void ApplyLevelName(Level level, string requested, string paramName = "name") =>
        Apply(() => level.Name, v => level.Name = v, requested, paramName, "level name",
            () => new FilteredElementCollector(level.Document)
                .OfClass(typeof(Level))
                .Where(l => l.Id != level.Id &&
                            string.Equals(l.Name, requested, StringComparison.OrdinalIgnoreCase))
                .Select(l => $"A level named '{l.Name}' already exists (id {l.Id.Value}).")
                .FirstOrDefault());

    /// <summary>Names a grid. Grid names are unique across the document.</summary>
    internal static void ApplyGridName(Grid grid, string requested, string paramName = "name") =>
        Apply(() => grid.Name, v => grid.Name = v, requested, paramName, "grid name",
            () => new FilteredElementCollector(grid.Document)
                .OfClass(typeof(Grid))
                .Where(g => g.Id != grid.Id &&
                            string.Equals(g.Name, requested, StringComparison.OrdinalIgnoreCase))
                .Select(g => $"A grid named '{g.Name}' already exists (id {g.Id.Value}).")
                .FirstOrDefault());

    /// <summary>
    /// Sets a room's Name parameter. Read back from <c>ROOM_NAME</c>, not <c>Room.Name</c>, which
    /// reports name and number together. Revit allows duplicate room names, so there is no clash.
    /// </summary>
    internal static void ApplyRoomName(Autodesk.Revit.DB.Architecture.Room room, string requested,
        string paramName = "name") =>
        Apply(() => room.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString() ?? "",
              v => room.Name = v, requested, paramName, "room name", () => null);

    /// <summary>Sets a room's Number. Revit allows duplicates (it warns, it does not refuse).</summary>
    internal static void ApplyRoomNumber(Autodesk.Revit.DB.Architecture.Room room, string requested,
        string paramName = "number") =>
        Apply(() => room.Number, v => room.Number = v, requested, paramName, "room number", () => null);

    /// <summary>
    /// Sets the value or throws a <see cref="RevitCommandException"/>: <c>invalid_chars</c> (400),
    /// <c>name_collision</c> (409), or <c>invalid_parameter</c> (400) for anything else Revit refused.
    /// </summary>
    private static void Apply(
        Func<string> get, Action<string> set, string requested, string paramName, string what,
        Func<string?> describeClash)
    {
        try
        {
            set(requested);
        }
        catch (Autodesk.Revit.Exceptions.ApplicationException ex)
        {
            var bad = IllegalCharsIn(requested);
            if (bad.Length > 0)
                throw new RevitCommandException("invalid_chars",
                    $"Parameter '{paramName}' contains character(s) Revit does not allow in a {what}: " +
                    string.Join(" ", bad) + ". Revit disallows: " + string.Join(" ", IllegalChars) + ".");

            var clash = describeClash();
            if (clash is not null)
                throw new RevitCommandException("name_collision", clash);

            throw new RevitCommandException("invalid_parameter",
                $"Revit rejected the {what} '{requested}': {ex.Message}");
        }

        // Belt and braces: a setter that returns without throwing must still have applied it.
        var actual = get();
        if (!string.Equals(actual, requested, StringComparison.Ordinal))
            throw new RevitCommandException("invalid_parameter",
                $"Revit kept the {what} '{actual}' instead of the requested '{requested}'.");
    }
}
