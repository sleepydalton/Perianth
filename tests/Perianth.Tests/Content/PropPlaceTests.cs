using System;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Perianth.Core;
using Perianth.Core.Content;
using Perianth.Formats.Diagnostics;
using Perianth.Formats.Io;
using Xunit;

namespace Perianth.Tests.Content;

/// <summary>
/// Placing a prop by copying one already in the layer.
/// </summary>
/// <remarks>
/// The fixtures are invented, as the repository requires. The shapes are the
/// measured ones: a prop record carrying a matrix, a resource and a sphere
/// radius, and a second entity of a type that is not a prop.
/// </remarks>
public sealed class PropPlaceTests
{
    // The identifiers below are invented -- repeated nibbles, and the hex
    // digits counted up and back down. A uid is 128 opaque bits, so a fixture
    // needs one that is well-formed rather than one that is real, and the
    // game's own belong here no more than its textures do.
    //
    // This line is read by the content scan, which cannot tell an invented
    // identifier from a real one and so takes the claim from whoever wrote it.
    //
    // scan-ok: identifiers here are invented

    private const string Prop =
        "\t{\n" +
        "\t\tDepthGroupChoice = \"Background\",\n" +
        "\t\taiInteract = true,\n" +
        "\t\tmatrix = {\n" +
        "\t\t\t0 0 1 34.0588,\n" +
        "\t\t\t0 1 0 -0.15,\n" +
        "\t\t\t-1 0 0 -183.832,\n" +
        "\t\t\t0 0 0 1,\n" +
        "\t\t},\n" +
        "\t\tname = \"made_up_sink\",\n" +
        "\t\tresource = F\"camel/graph objects/prop/made_up_sink.mgraphobject\",\n" +
        "\t\tsphereRadius = 30.41,\n" +
        "\t\ttype = \"Prop\",\n" +
        "\t\tuid = #0123456789ABCDEF0123456789ABCDEF,\n" +
        "\t},\n";

    private const string Waypoint =
        "\t{\n" +
        "\t\tmatrix = {\n\t\t\t1 0 0 0,\n\t\t\t0 1 0 0,\n\t\t\t0 0 1 0,\n\t\t\t0 0 0 1,\n\t\t},\n" +
        "\t\tname = \"made_up_waypoint\",\n" +
        "\t\ttype = \"CWaypoint\",\n" +
        "\t\tuid = #FEDCBA9876543210FEDCBA9876543210,\n" +
        "\t},\n";

    /// <summary>
    /// A prop whose <c>children</c> block holds a record with a name and a uid
    /// of its own. 3,536 shipped entities carry one.
    /// </summary>
    private const string Parent =
        "\t{\n" +
        "\t\tchildren = {\n" +
        "\t\t\t{\n\t\t\t\tname = \"made_up_child\",\n\t\t\t\tuid = #AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA,\n\t\t\t},\n" +
        "\t\t},\n" +
        "\t\tmatrix = {\n\t\t\t1 0 0 5,\n\t\t\t0 1 0 6,\n\t\t\t0 0 1 7,\n\t\t\t0 0 0 1,\n\t\t},\n" +
        "\t\tname = \"made_up_parent\",\n" +
        "\t\tresource = F\"camel/graph objects/prop/made_up_parent.mgraphobject\",\n" +
        "\t\ttype = \"Prop\",\n" +
        "\t\tuid = #11111111111111111111111111111111,\n" +
        "\t},\n";

    private static readonly string Chunk = "{\n" + Prop + Waypoint + Parent + "}";

    private static readonly string Layer =
        "{\n\tcontent = {\n\t\tquadTreeHeader = {\n\t\t\t[7] = {\n" +
        string.Create(CultureInfo.InvariantCulture, $"\t\t\t\toffset = 0,\n\t\t\t\tsize = {Chunk.Length},\n") +
        "\t\t\t},\n\t\t},\n\t},\n\theader = {\n\t\tentities = 3,\n\t},\n}\n\0" + Chunk;

    private static SourceFile File() =>
        SourceFile.FromMemory("layerdata.mlayer", Encoding.Latin1.GetBytes(Layer));

    private static Result<PropPlacement> Place(
        string template = "made_up_sink",
        string name = "brand_new_prop",
        string graphObject = "camel/graph objects/prop/brand_new.mgraphobject") =>
        PropPlace.Beside(File(), template, name, graphObject, new PropPosition(1.5, -2, 3.25));

    private static string Text(PropPlacement placement) =>
        Encoding.Latin1.GetString(placement.Layer.Span);

    [Fact]
    public void The_layers_entities_are_listed_with_what_they_draw()
    {
        ImmutableArray<LayerEntity> entities = PropPlace.List(File()).Value;

        Assert.Equal(3, entities.Length);
        Assert.Equal("made_up_sink", entities[0].Name);
        Assert.Equal("Prop", entities[0].Type);
        Assert.Equal("camel/graph objects/prop/made_up_sink.mgraphobject", entities[0].Resource);
        Assert.Equal("CWaypoint", entities[1].Type);
        Assert.Null(entities[1].Resource);
    }

    [Fact]
    public void An_entity_reports_where_it_stands()
    {
        // So a copy can start beside the thing it copies. The map's origin is
        // nowhere in particular, and typing coordinates blind is not something
        // anyone can do.
        ImmutableArray<LayerEntity> entities = PropPlace.List(File()).Value;

        Assert.Equal(34.0588, entities[0].Stands.X, 4);
        Assert.Equal(-0.15, entities[0].Stands.Y, 4);
        Assert.Equal(-183.832, entities[0].Stands.Z, 4);
        Assert.Equal(new PropPosition(5, 6, 7), entities[2].Stands);
    }

    [Fact]
    public void Every_field_the_operation_does_not_name_comes_from_the_template()
    {
        // The whole reason a record is copied rather than built. A prop carries
        // 21 fields in its commonest form and the corpus holds 22 sets of them.
        string text = Text(Place().Value);

        Assert.Contains("\t\tDepthGroupChoice = \"Background\",\n", text, StringComparison.Ordinal);
        Assert.Contains("\t\taiInteract = true,\n", text, StringComparison.Ordinal);
        Assert.Contains("\t\tsphereRadius = 30.41,\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Only_the_position_moves_and_the_basis_survives()
    {
        // The matrix's translation is its last column; the other twelve numbers
        // are a rotation. Rewriting all sixteen would silently stand a turned
        // prop up straight.
        string text = Text(Place().Value);

        Assert.Contains(
            "\t\tmatrix = {\n\t\t\t0 0 1 1.5,\n\t\t\t0 1 0 -2,\n\t\t\t-1 0 0 3.25,\n\t\t\t0 0 0 1,\n\t\t},\n",
            text,
            StringComparison.Ordinal);

        // And the template still stands where it did.
        Assert.Contains("\t\t\t0 0 1 34.0588,\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_name_the_resource_and_the_uid_are_the_things_that_change()
    {
        PropPlacement placed = Place().Value;
        string text = Text(placed);

        Assert.Contains("\t\tname = \"brand_new_prop\",\n", text, StringComparison.Ordinal);
        Assert.Contains(
            "\t\tresource = F\"camel/graph objects/prop/brand_new.mgraphobject\",\n",
            text,
            StringComparison.Ordinal);
        Assert.Contains($"\t\tuid = #{placed.Uid},\n", text, StringComparison.Ordinal);

        // The template keeps its own three.
        Assert.Contains("\t\tname = \"made_up_sink\",\n", text, StringComparison.Ordinal);
        Assert.Contains("#0123456789ABCDEF0123456789ABCDEF", text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_uid_is_minted_from_the_name_and_is_the_same_every_time()
    {
        // Determinism is the product: a random uid makes a mod unreproducible
        // and its patches unstable.
        Assert.Equal(Place().Value.Uid, Place().Value.Uid);
        Assert.NotEqual(Place().Value.Uid, Place(name: "other").Value.Uid);
        Assert.Equal(32, Place().Value.Uid.Length);
    }

    [Fact]
    public void The_copy_lands_in_the_templates_own_chunk()
    {
        // So it sits in a quad-tree cell the game already loads props from,
        // which asks nothing of the loader that the shipped data does not.
        Assert.Equal(0, Place().Value.Chunk);
        Assert.Equal(3, PropPlace.List(File()).Value.Length);
        Assert.Equal(4, PropPlace.List(
            SourceFile.FromMemory("layerdata.mlayer", Place().Value.Layer.ToArray())).Value.Length);
    }

    [Fact]
    public void A_carried_sphere_radius_is_reported()
    {
        // The MMB bounding-box failure reached by another route: a culling bound
        // describing the template's model, and an offline render cannot show it
        // because it does not cull.
        PropPlacement placed = Place().Value;

        Assert.Contains(
            placed.Diagnostics,
            d => d.Message.Contains("sphereRadius", StringComparison.Ordinal)
                && d.Message.Contains("cull", StringComparison.Ordinal));
        Assert.Contains(
            placed.Diagnostics,
            d => d.Message.Contains("DepthGroupChoice", StringComparison.Ordinal));
    }

    [Fact]
    public void Copying_something_that_is_not_a_prop_is_refused()
    {
        // A waypoint has no graph object and no shape, so the copy would be a
        // prop in name only — which looks in the file exactly like one that
        // works.
        Result<PropPlacement> result = Place(template: "made_up_waypoint");

        Assert.False(result.IsSuccess);
        Assert.Equal(RefusalKind.Unsupported, result.Refusal.Kind);
        Assert.Contains("CWaypoint", result.Refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_template_the_layer_does_not_hold_is_refused()
    {
        Result<PropPlacement> result = Place(template: "no_such_prop");

        Assert.False(result.IsSuccess);
        Assert.Equal(RefusalKind.Unsupported, result.Refusal.Kind);
    }

    [Fact]
    public void A_name_the_layer_already_uses_is_refused() =>
        Assert.False(Place(name: "made_up_sink").IsSuccess);

    [Theory]
    [InlineData("camel/models/made_up.mmb")]
    [InlineData("camel/graph objects/prop/made_up")]
    public void A_resource_that_is_not_a_graph_object_is_refused(string path)
    {
        // The commonest mistake available here: a prop names the graph object,
        // and the graph object names the model. Handing it the .mmb produces a
        // prop that draws nothing.
        Result<PropPlacement> result = Place(graphObject: path);

        Assert.False(result.IsSuccess);
        Assert.Contains(".mgraphobject", result.Refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_name_is_refused() => Assert.False(Place(name: string.Empty).IsSuccess);

    [Fact]
    public void A_position_that_is_not_a_number_is_refused() =>
        Assert.False(PropPlace.Beside(
            File(), "made_up_sink", "brand_new", "a.mgraphobject",
            new PropPosition(double.NaN, 0, 0)).IsSuccess);

    [Fact]
    public void A_nested_child_is_neither_listed_nor_renamed()
    {
        // A prop's children block holds records with names and uids of their
        // own, at deeper indentation. Matching a field anywhere in the record
        // would rename somebody else's entity and leave this one alone — and
        // the file would still parse.
        PropPlacement placed = Place(template: "made_up_parent").Value;
        string text = Text(placed);

        Assert.DoesNotContain(
            PropPlace.List(File()).Value, e => e.Name.Equals("made_up_child", StringComparison.Ordinal));

        // Two of them now: the template's and the copy's, both untouched.
        Assert.Equal(2, Occurrences(text, "name = \"made_up_child\""));
        Assert.Equal(2, Occurrences(text, "#AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"));
        Assert.Contains("\t\tname = \"brand_new_prop\",\n", text, StringComparison.Ordinal);
    }

    private static int Occurrences(string text, string needle)
    {
        int count = 0;
        for (int at = text.IndexOf(needle, StringComparison.Ordinal);
             at >= 0;
             at = text.IndexOf(needle, at + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    [Fact]
    public void The_edited_layer_still_accounts_for_its_body()
    {
        // The insertion grew a chunk, so the header's size had to move with it.
        // Re-reading is what proves it did: the reader refuses a layer whose
        // chunks do not tile its body.
        Result<ImmutableArray<LayerEntity>> reread = PropPlace.List(
            SourceFile.FromMemory("layerdata.mlayer", Place().Value.Layer.ToArray()));

        Assert.True(reread.IsSuccess);
    }
}
