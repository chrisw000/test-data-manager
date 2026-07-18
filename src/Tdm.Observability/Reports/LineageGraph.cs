using System.Globalization;
using System.Net;
using System.Text;
using Tdm.Core.Manifest;

namespace Tdm.Observability.Reports;

/// <summary>
/// Builds the reference lineage graph for the HTML report (W4-D1): created entities as
/// nodes, manifest references as edges labelled with <c>resolvedFrom</c> — the cross-domain
/// identity story made visible. Bulk creates collapse into one aggregate node per
/// (scenario, entity) so the graph stays readable at W3 bulk volumes, and nodes are merged
/// across scenarios/domains by persisted id so a Billing invoice's edge lands on the Orders
/// customer that owns the identity.
/// </summary>
internal static class LineageGraph
{
    private const int NodeWidth = 176;
    private const int NodeHeight = 44;
    private const int ColumnGap = 110;
    private const int RowGap = 18;
    private const int Margin = 16;

    private sealed class Node
    {
        public required string Id { get; init; }
        public required string Title { get; init; }
        public required string Subtitle { get; init; }
        public string Domain { get; init; } = "";
        public bool External { get; init; }
        public int Depth { get; set; }
        public double X { get; set; }
        public double Y { get; set; }
    }

    private sealed class Edge
    {
        public required string Source { get; init; }
        public required string Target { get; init; }
        public required string Label { get; init; }
        public int Count { get; set; } = 1;
    }

    /// <summary>Renders the graph as an inline SVG, or an explanatory paragraph when the
    /// manifest holds no lineage (no created entities and no references).</summary>
    public static string RenderSvg(RunManifest manifest, int bulkAggregationThreshold)
    {
        var (nodes, edges) = Build(manifest, bulkAggregationThreshold);
        if (nodes.Count == 0)
            return "<p class=\"empty\">No entities or references were recorded in this run.</p>";

        Layout(nodes, edges);

        var width = nodes.Values.Max(n => n.X) + NodeWidth + Margin;
        var height = nodes.Values.Max(n => n.Y) + NodeHeight + Margin;
        var domains = nodes.Values.Where(n => n.Domain.Length > 0).Select(n => n.Domain)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var svg = new StringBuilder();
        svg.Append(FormattableString.Invariant(
            $"<svg class=\"lineage\" viewBox=\"0 0 {width:0} {height:0}\" role=\"img\" aria-label=\"Reference lineage graph\">"));
        svg.Append("<defs><marker id=\"arrow\" viewBox=\"0 0 8 8\" refX=\"7\" refY=\"4\" markerWidth=\"7\" markerHeight=\"7\" orient=\"auto-start-reverse\">"
                   + "<path d=\"M0,0 L8,4 L0,8 z\" class=\"arrow-head\"/></marker></defs>");

        foreach (var edge in edges)
        {
            var source = nodes[edge.Source];
            var target = nodes[edge.Target];
            // Dependencies sit in lower-depth columns, so the arrow leaves the source's left
            // edge and lands on the target's right edge.
            var sx = source.X;
            var sy = source.Y + NodeHeight / 2d;
            var tx = target.X + NodeWidth;
            var ty = target.Y + NodeHeight / 2d;
            var bend = Math.Max(30, (sx - tx) / 2);
            var cls = edge.Label switch
            {
                "contextBag" => "edge-ctx",
                "database" => "edge-db",
                "identityContract" => "edge-idc",
                _ => "edge-other",
            };
            svg.Append(FormattableString.Invariant(
                $"<path class=\"edge {cls}\" d=\"M{sx:0.#},{sy:0.#} C{sx - bend:0.#},{sy:0.#} {tx + bend:0.#},{ty:0.#} {tx:0.#},{ty:0.#}\" marker-end=\"url(#arrow)\"/>"));
            var label = edge.Count > 1 ? $"{edge.Label} ×{edge.Count}" : edge.Label;
            svg.Append(FormattableString.Invariant(
                $"<text class=\"edge-label {cls}\" x=\"{(sx + tx) / 2:0.#}\" y=\"{(sy + ty) / 2 - 5:0.#}\" text-anchor=\"middle\">{Escape(label)}</text>"));
        }

        foreach (var node in nodes.Values)
        {
            var domainClass = node.Domain.Length > 0
                ? $"dom-{domains.FindIndex(d => string.Equals(d, node.Domain, StringComparison.OrdinalIgnoreCase)) % 8}"
                : "dom-none";
            svg.Append(FormattableString.Invariant(
                $"<g class=\"node {domainClass}{(node.External ? " external" : "")}\">"));
            svg.Append(FormattableString.Invariant(
                $"<rect x=\"{node.X:0.#}\" y=\"{node.Y:0.#}\" width=\"{NodeWidth}\" height=\"{NodeHeight}\" rx=\"6\"/>"));
            svg.Append(FormattableString.Invariant(
                $"<text class=\"node-title\" x=\"{node.X + 10:0.#}\" y=\"{node.Y + 19:0.#}\">{Escape(Clip(node.Title, 22))}</text>"));
            svg.Append(FormattableString.Invariant(
                $"<text class=\"node-sub\" x=\"{node.X + 10:0.#}\" y=\"{node.Y + 34:0.#}\">{Escape(Clip(node.Subtitle, 26))}</text>"));
            svg.Append(FormattableString.Invariant(
                $"<title>{Escape(node.Title)} — {Escape(node.Subtitle)}</title></g>"));
        }

        svg.Append("</svg>");

        var legend = new StringBuilder("<div class=\"legend\">");
        foreach (var (cls, name) in new[] { ("edge-ctx", "contextBag"), ("edge-db", "database"), ("edge-idc", "identityContract") })
            legend.Append($"<span class=\"legend-item\"><span class=\"legend-line {cls}\"></span>{name}</span>");
        for (var i = 0; i < domains.Count; i++)
            legend.Append($"<span class=\"legend-item\"><span class=\"legend-swatch dom-{i % 8}\"></span>{Escape(domains[i])}</span>");
        legend.Append("<span class=\"legend-item\"><span class=\"legend-swatch external\"></span>external / not created in this run</span></div>");

        return "<div class=\"lineage-scroll\">" + svg + "</div>" + legend;
    }

    private static (Dictionary<string, Node> Nodes, List<Edge> Edges) Build(RunManifest manifest, int bulkAggregationThreshold)
    {
        var nodes = new Dictionary<string, Node>(StringComparer.Ordinal);
        var edges = new Dictionary<(string, string, string), Edge>();
        // Lookups used to merge reference targets onto created entities: persisted id is
        // authoritative (the identity contract makes ids equal across domains); the
        // entity+naturalKey pair is the fallback for db-generated ids.
        var byPersistedId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var byEntityKey = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var byOrdinal = new Dictionary<(int Scenario, int Ordinal), string>();

        for (var si = 0; si < manifest.Scenarios.Count; si++)
        {
            var scenario = manifest.Scenarios[si];
            var created = scenario.Entities.Where(e => e.Verb is "Create" or "Projection").ToList();

            foreach (var group in created.GroupBy(e => (e.Entity, e.Domain)))
            {
                var members = group.ToList();
                var bulk = scenario.BulkOperations.FirstOrDefault(b =>
                    string.Equals(b.Entity, group.Key.Entity, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(b.Domain, group.Key.Domain, StringComparison.OrdinalIgnoreCase));
                var total = bulk?.Count ?? members.Count;

                if (bulk is not null || members.Count >= bulkAggregationThreshold)
                {
                    // One aggregate node per (scenario, entity): "500 × Product" (§6 risk table).
                    var id = $"agg:{si}:{group.Key.Domain}:{group.Key.Entity}";
                    nodes[id] = new Node
                    {
                        Id = id,
                        Title = FormattableString.Invariant($"{total} × {group.Key.Entity}"),
                        Subtitle = group.Key.Domain,
                        Domain = group.Key.Domain,
                    };
                    foreach (var member in members)
                    {
                        byOrdinal[(si, member.Ordinal)] = id;
                        if (member.Id is { Length: > 0 } pid) byPersistedId.TryAdd(pid, id);
                    }
                }
                else
                {
                    foreach (var member in members)
                    {
                        var key = member.NaturalKey ?? member.Id;
                        // Same domain+entity+naturalKey in two scenarios is the same row
                        // (idempotent create-or-reuse) — merge into one node.
                        var id = $"ent:{member.Domain}:{member.Entity}:{key ?? FormattableString.Invariant($"{si}#{member.Ordinal}")}";
                        nodes.TryAdd(id, new Node
                        {
                            Id = id,
                            Title = member.Entity,
                            Subtitle = key is null ? member.Domain : $"{key} · {member.Domain}",
                            Domain = member.Domain,
                        });
                        byOrdinal[(si, member.Ordinal)] = id;
                        if (member.Id is { Length: > 0 } pid) byPersistedId.TryAdd(pid, id);
                        if (member.NaturalKey is { Length: > 0 } nk)
                            byEntityKey.TryAdd($"{member.Entity}:{nk}", id);
                    }
                }
            }
        }

        for (var si = 0; si < manifest.Scenarios.Count; si++)
        {
            foreach (var reference in manifest.Scenarios[si].References)
            {
                var targetId = ResolveTarget(reference, nodes, byPersistedId, byEntityKey);
                if (reference.SourceOrdinal is not { } ordinal ||
                    !byOrdinal.TryGetValue((si, ordinal), out var sourceId) ||
                    sourceId == targetId)
                {
                    continue; // external declarations (and pre-W4 manifests) contribute the node only
                }
                var key = (sourceId, targetId, reference.ResolvedFrom);
                if (edges.TryGetValue(key, out var existing)) existing.Count++;
                else edges[key] = new Edge { Source = sourceId, Target = targetId, Label = reference.ResolvedFrom };
            }
        }

        return (nodes, [.. edges.Values]);
    }

    private static string ResolveTarget(ReferenceManifest reference, Dictionary<string, Node> nodes,
        Dictionary<string, string> byPersistedId, Dictionary<string, string> byEntityKey)
    {
        if (reference.Id is { Length: > 0 } id && byPersistedId.TryGetValue(id, out var byId)) return byId;
        if (byEntityKey.TryGetValue(reference.Target, out var byKey)) return byKey;

        // Not created in this run: base seed data resolved from the database, or a
        // cross-domain identity the owning run seeded elsewhere.
        var separator = reference.Target.IndexOf(':');
        var entity = separator > 0 ? reference.Target[..separator] : reference.Target;
        var naturalKey = separator > 0 ? reference.Target[(separator + 1)..] : "";
        var nodeId = $"ext:{reference.OwningDomain}:{reference.Target}";
        nodes.TryAdd(nodeId, new Node
        {
            Id = nodeId,
            Title = entity,
            Subtitle = reference.OwningDomain is { Length: > 0 } owner ? $"{naturalKey} · {owner}" : naturalKey,
            Domain = reference.OwningDomain ?? "",
            External = true,
        });
        if (reference.Id is { Length: > 0 } persistedId) byPersistedId.TryAdd(persistedId, nodeId);
        byEntityKey.TryAdd(reference.Target, nodeId);
        return nodeId;
    }

    /// <summary>Layered layout: a node's column is its dependency depth (referenced targets
    /// sit left of the entities that reference them), rows stack in insertion order.</summary>
    private static void Layout(Dictionary<string, Node> nodes, List<Edge> edges)
    {
        var outgoing = edges.GroupBy(e => e.Source)
            .ToDictionary(g => g.Key, g => g.Select(e => e.Target).ToList());
        var depths = new Dictionary<string, int>();
        var visiting = new HashSet<string>();

        int Depth(string id)
        {
            if (depths.TryGetValue(id, out var known)) return known;
            if (!visiting.Add(id)) return 0; // cycle guard — self-referential data still renders
            var depth = outgoing.TryGetValue(id, out var targets) && targets.Count > 0
                ? 1 + targets.Max(Depth)
                : 0;
            visiting.Remove(id);
            return depths[id] = depth;
        }

        foreach (var node in nodes.Values) node.Depth = Depth(node.Id);

        foreach (var column in nodes.Values.GroupBy(n => n.Depth))
        {
            var row = 0;
            foreach (var node in column)
            {
                node.X = Margin + node.Depth * (NodeWidth + ColumnGap);
                node.Y = Margin + row++ * (NodeHeight + RowGap);
            }
        }
    }

    private static string Clip(string text, int max) =>
        text.Length <= max ? text : text[..(max - 1)] + "…";

    private static string Escape(string text) => WebUtility.HtmlEncode(text);
}
