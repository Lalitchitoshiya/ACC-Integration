using System.Globalization;

namespace Connector.Api.Metadata;

/// <summary>
/// Display metadata for WS Pro columns carried through to the ACC property palette
/// (specs/14 Track B, property work Stages 2-3).
///
/// Raw column names are what WS Pro's exporter emits — `iwlive_can_be_closed`,
/// `active_total_ratio`, `hazen_williams` — which are fine as data but read poorly in a
/// properties panel next to "Length" and "Diameter". This maps the ones we understand
/// onto a human label, a group, and where it can be justified a unit.
///
/// Deliberately a *hybrid*: a column with no entry here still reaches ACC, under its raw
/// name and in the catch-all group. Nothing is dropped for want of a catalogue entry, and
/// the raw names remaining in the palette are a visible to-do list of fields still worth
/// curating.
///
/// On units — the rule is that a WRONG unit is worse than none. Mislabelling a value is
/// precisely the class of bug that made a 5280 ft pipe display as "5280 m" earlier in this
/// project, and a reviewer cannot tell a mislabelled number from a correct one. So a unit
/// is attached only where it is verifiable from the data or unambiguous in the underlying
/// standard; the genuinely ambiguous ones (reaction coefficients, roughness heights)
/// carry a clear label and no unit rather than a guess.
/// </summary>
public static class PropertyCatalogue
{
    /// <summary>
    /// IFC property set names. Each becomes its own collapsible group in the Viewer's
    /// property panel, which is the whole point: a pump carries 18 properties spanning
    /// identity, hydraulics, water quality and electrical operation, and as one flat list
    /// that is already hard to read — a real utility model with 40+ populated columns
    /// would be unusable.
    ///
    /// Hydraulics keeps the original name so the pset that already shipped stays stable.
    /// </summary>
    public static class Group
    {
        public const string Identity = "Pset_ACCWaterIdentity";
        public const string Hydraulics = "Pset_ACCWaterHydraulics";
        public const string Levels = "Pset_ACCWaterLevels";
        public const string Quality = "Pset_ACCWaterQuality";
        public const string Operations = "Pset_ACCWaterOperations";
        public const string Asset = "Pset_ACCWaterAsset";
        /// <summary>Anything not yet catalogued — visible rather than hidden.</summary>
        public const string Other = "Pset_ACCWaterOther";
    }

    /// <summary>Display order in the palette; groups not listed sort last.</summary>
    public static readonly string[] GroupOrder =
    [
        Group.Identity, Group.Hydraulics, Group.Levels,
        Group.Quality, Group.Operations, Group.Asset, Group.Other,
    ];

    public readonly record struct Entry(string Label, string? Unit, string Group);

    // Verified against the sample exports this session:
    //   levels/heads  — Tank 1 bottom_level 40.20312 m == the INP's 131.9 ft, so metres
    //   duty_head     — 101.6 m and 31.7 m, both plausible pump heads, and metres
    //                   matches the project length unit WS Pro exports in
    //   wave_celerity — 1400.0, the standard water-hammer wave speed in m/s
    // Left unitless on purpose:
    //   bulk_coeff / wall_coeff — EPANET reaction coefficient units depend on the
    //     reaction order (zero-order is mass/volume/day, first-order is 1/day; wall
    //     coefficients differ again), and the order is a model-level setting we do not
    //     read here. Labelled, not guessed.
    //   darcy_weissbach / colebrook_white — roughness heights, conventionally mm, but the
    //     0.015 seen in the samples is a pump-station placeholder rather than a measured
    //     value, so there is nothing here to confirm it against.
    //   pressure_rating — could be bar, a pipe class, or a nominal rating string.
    private static readonly Dictionary<string, Entry> Entries = new(StringComparer.OrdinalIgnoreCase)
    {
        // ---- Identity / classification ----
        ["asset_uid"] = new("Asset UID", null, Group.Identity),
        ["system_type"] = new("System Type", null, Group.Identity),
        ["total_connections"] = new("Total Connections", null, Group.Identity),
        ["area"] = new("Area", null, Group.Identity),
        ["isolation_area"] = new("Isolation Area", null, Group.Identity),

        // ---- Levels ----
        ["ground_level"] = new("Ground Level", "m", Group.Levels),
        ["highest_property"] = new("Highest Property", "m", Group.Levels),
        ["bottom_level"] = new("Bottom Level", "m", Group.Levels),
        ["minimum_level"] = new("Minimum Level", "m", Group.Levels),
        ["top_level"] = new("Top Level", "m", Group.Levels),
        ["initial_level"] = new("Initial Level", "m", Group.Levels),
        ["level"] = new("Level", "m", Group.Levels),
        ["plan_area"] = new("Plan Area", "m2", Group.Levels),
        ["us_invert"] = new("Upstream Invert", "m", Group.Levels),
        ["ds_invert"] = new("Downstream Invert", "m", Group.Levels),

        // ---- Pipe hydraulics ----
        ["roughness_type"] = new("Roughness Type", null, Group.Hydraulics),
        ["hazen_williams"] = new("Hazen-Williams C", null, Group.Hydraulics),
        ["modified_hazen_williams"] = new("Modified Hazen-Williams C", null, Group.Hydraulics),
        ["darcy_weissbach"] = new("Darcy-Weisbach Roughness", null, Group.Hydraulics),
        ["colebrook_white"] = new("Colebrook-White Roughness", null, Group.Hydraulics),
        ["k"] = new("Roughness k", null, Group.Hydraulics),
        ["local_loss"] = new("Local Loss Coefficient", null, Group.Hydraulics),
        ["wave_celerity"] = new("Wave Celerity", "m/s", Group.Hydraulics),
        ["demand"] = new("Demand", null, Group.Hydraulics),
        ["shape"] = new("Channel Shape", null, Group.Hydraulics),
        ["channel_height"] = new("Channel Height", "mm", Group.Hydraulics),
        ["channel_width"] = new("Channel Width", "mm", Group.Hydraulics),

        // ---- Water quality ----
        ["bulk_coeff"] = new("Bulk Reaction Coefficient", null, Group.Quality),
        ["wall_coeff"] = new("Wall Reaction Coefficient", null, Group.Quality),

        // ---- Operation ----
        ["duty_head"] = new("Duty Head", "m", Group.Operations),
        ["power_consumption"] = new("Power Consumption", "kW", Group.Operations),
        ["nominal_speed"] = new("Nominal Speed", "rpm", Group.Operations),
        ["nominal_flow"] = new("Nominal Flow", null, Group.Operations),
        ["suction_diameter"] = new("Suction Diameter", "mm", Group.Operations),
        ["pressure_diameter"] = new("Delivery Diameter", "mm", Group.Operations),
        ["active_total_ratio"] = new("Active / Total Ratio", null, Group.Operations),
        ["electric_hydraulic_ratio"] = new("Electric / Hydraulic Ratio", null, Group.Operations),
        ["electricity_tariff"] = new("Electricity Tariff", null, Group.Operations),
        ["voltage"] = new("Voltage", null, Group.Operations),
        ["bypass"] = new("Bypass", null, Group.Operations),
        ["turbine"] = new("Turbine", null, Group.Operations),
        ["iwlive_can_be_closed"] = new("IWLive Can Be Closed", null, Group.Operations),

        // ---- Asset / condition ----
        ["pressure_rating"] = new("Pressure Rating", null, Group.Asset),
        ["construction_date"] = new("Construction Date", null, Group.Asset),
        ["year"] = new("Year Laid", null, Group.Asset),
        ["lining"] = new("Lining", null, Group.Asset),
        ["notes"] = new("Notes", null, Group.Asset),
    };

    /// <summary>Display name for a column — the curated label, or the raw column name
    /// when we have not catalogued it yet.</summary>
    public static string LabelFor(string column) =>
        Entries.TryGetValue(column, out var e) ? e.Label : column;

    /// <summary>Property set a column belongs in; uncatalogued columns land in Other so
    /// they stay visible instead of being silently folded into a group they don't suit.</summary>
    public static string GroupFor(string column) =>
        Entries.TryGetValue(column, out var e) ? e.Group : Group.Other;

    /// <summary>
    /// The value as it should read in the palette, with its unit when one is known.
    ///
    /// Decimal values are re-formatted to at most six places. WS Pro stores many fields
    /// as 32-bit floats, so a converted 147 ft arrives as "44.805600000179226" and a
    /// reaction coefficient as "-0.30000001192092896" — conversion noise, not precision.
    /// Shown raw it reads as a defect, and it sat inconsistently beside the curated
    /// Elevation, which already formats the same underlying number as "44.8056".
    ///
    /// Only values containing a decimal point are touched: integers pass through
    /// verbatim, so a long numeric asset id can never be mangled by a double round-trip.
    /// Six places is sub-micron on a metre value — display tidying, not data loss.
    /// </summary>
    public static string DisplayValue(string column, string value)
    {
        var text = value.Contains('.')
                   && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
            ? d.ToString("0.######", CultureInfo.InvariantCulture)
            : value;
        return Entries.TryGetValue(column, out var e) && e.Unit is not null
            ? $"{text} {e.Unit}"
            : text;
    }
}
