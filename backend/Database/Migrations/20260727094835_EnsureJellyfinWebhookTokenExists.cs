using Microsoft.EntityFrameworkCore.Migrations;
using NzbWebDAV.Utils;

#nullable disable

namespace NzbWebDAV.Database.Migrations
{
    /// <inheritdoc />
    public partial class EnsureJellyfinWebhookTokenExists : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Not InsertData: this must tolerate re-running against a database where the
            // row already exists (e.g. a prior attempt at this migration got as far as the
            // insert but was interrupted before the migration-history row committed), or a
            // second application instance racing this same migration. An unconditional
            // insert throws "UNIQUE constraint failed: ConfigItems.ConfigName" and crash-loops
            // the app forever in that case, since the migration is never recorded as applied
            // and is retried identically on every restart.
            var token = GuidUtil.GenerateSecureGuid().ToString("N");
            migrationBuilder.Sql(
                $"""
                INSERT INTO ConfigItems (ConfigName, ConfigValue)
                SELECT 'jellyfin.webhook-token', '{token}'
                WHERE NOT EXISTS (SELECT 1 FROM ConfigItems WHERE ConfigName = 'jellyfin.webhook-token');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Intentionally left blank
        }
    }
}
