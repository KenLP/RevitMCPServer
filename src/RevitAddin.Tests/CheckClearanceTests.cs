using RevitMCPAddin.Commands;
using Xunit;

namespace RevitMCPAddin.Tests;

/// <summary>
/// Pure tests for CheckClearanceCommand's AABB overlap logic.
/// Uses the double-coordinate overload so no Revit runtime is required.
/// </summary>
public class CheckClearanceTests
{
    private static bool Intersects(
        double minAX, double minAY, double minAZ,
        double maxAX, double maxAY, double maxAZ,
        double minBX, double minBY, double minBZ,
        double maxBX, double maxBY, double maxBZ) =>
        CheckClearanceCommand.BboxIntersects(
            minAX, minAY, minAZ, maxAX, maxAY, maxAZ,
            minBX, minBY, minBZ, maxBX, maxBY, maxBZ);

    [Fact]
    public void Overlapping_boxes_intersect()
        => Assert.True(Intersects(0, 0, 0, 2, 2, 2,  1, 1, 1, 3, 3, 3));

    [Fact]
    public void Separate_boxes_do_not_intersect()
        => Assert.False(Intersects(0, 0, 0, 1, 1, 1,  2, 2, 2, 3, 3, 3));

    [Fact]
    public void Touching_at_face_counts_as_intersection()
        // Boxes share exactly the plane x=1
        => Assert.True(Intersects(0, 0, 0, 1, 1, 1,  1, 0, 0, 2, 1, 1));

    [Fact]
    public void Contained_box_intersects()
        => Assert.True(Intersects(0, 0, 0, 10, 10, 10,  2, 2, 2, 4, 4, 4));

    [Fact]
    public void Separated_on_z_axis_only()
        => Assert.False(Intersects(0, 0, 0, 2, 2, 1,  0, 0, 2, 2, 2, 3));

    [Fact]
    public void Separated_on_x_axis_only()
        => Assert.False(Intersects(0, 0, 0, 1, 5, 5,  2, 0, 0, 3, 5, 5));

    [Fact]
    public void Clearance_inflation_models_minimum_gap()
    {
        // Two boxes separated by 0.5 units on X — raw bboxes do NOT overlap.
        Assert.False(Intersects(0, 0, 0, 1, 1, 1,  1.5, 0, 0, 2.5, 1, 1));

        // After inflating setA by 0.6 (> 0.5 gap), should flag a clearance violation.
        double c = 0.6;
        Assert.True(Intersects(-c, -c, -c, 1 + c, 1 + c, 1 + c,  1.5, 0, 0, 2.5, 1, 1));
    }
}
