namespace FuzzyFindAnything.Core;

public sealed partial class Catalog
{
    private bool HasRegionSchema => Setting("schema_version") is "9" or "10";
    private bool HasExternalSchema => Setting("schema_version") == "10";
    private void EnsureRegionSchema()
    {
        Transaction(() =>
        {
            EnsureChunkSchema();
            foreach (string table in new[] { "image_representations", "job_passages", "vectors" })
                if (!Query($"PRAGMA table_info({table})", row => row.GetString(1)).Contains("region_json"))
                    Execute($"ALTER TABLE {table} ADD COLUMN region_json TEXT");
            if (!HasExternalSchema) SetSetting("schema_version", "9");
        });
    }
}
