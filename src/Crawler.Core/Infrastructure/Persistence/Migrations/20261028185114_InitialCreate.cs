using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Crawler.Core.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "crawl_jobs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    input_url = table.Column<string>(type: "text", nullable: false),
                    normalized_start_url = table.Column<string>(type: "text", nullable: false),
                    starting_host = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    max_depth = table.Column<int>(type: "integer", nullable: false),
                    max_pages = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    failure_reason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    discovered_pages = table.Column<int>(type: "integer", nullable: false),
                    processed_pages = table.Column<int>(type: "integer", nullable: false),
                    failed_pages = table.Column<int>(type: "integer", nullable: false),
                    truncated = table.Column<bool>(type: "boolean", nullable: false),
                    cancellation_requested = table.Column<bool>(type: "boolean", nullable: false),
                    cancellation_requested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    lease_owner = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    lease_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    correlation_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_crawl_jobs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "outbox_messages",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    job_id = table.Column<Guid>(type: "uuid", nullable: false),
                    message_type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    payload = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    published_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    publish_attempts = table.Column<int>(type: "integer", nullable: false),
                    last_error = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_outbox_messages", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "crawl_pages",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    job_id = table.Column<Guid>(type: "uuid", nullable: false),
                    normalized_url = table.Column<string>(type: "text", nullable: false),
                    depth = table.Column<int>(type: "integer", nullable: false),
                    fetch_status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    http_status_code = table.Column<int>(type: "integer", nullable: true),
                    domain_link_ratio = table.Column<double>(type: "double precision", nullable: false),
                    outgoing_link_count = table.Column<int>(type: "integer", nullable: false),
                    internal_link_count = table.Column<int>(type: "integer", nullable: false),
                    error_message = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    parent_page_id = table.Column<Guid>(type: "uuid", nullable: true),
                    discovery_order = table.Column<int>(type: "integer", nullable: false),
                    fetched_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_crawl_pages", x => x.id);
                    table.ForeignKey(
                        name: "fk_crawl_pages_crawl_jobs_job_id",
                        column: x => x.job_id,
                        principalTable: "crawl_jobs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_crawl_pages_crawl_pages_parent_page_id",
                        column: x => x.parent_page_id,
                        principalTable: "crawl_pages",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "crawl_edges",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    job_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_page_id = table.Column<Guid>(type: "uuid", nullable: false),
                    target_normalized_url = table.Column<string>(type: "text", nullable: false),
                    target_page_id = table.Column<Guid>(type: "uuid", nullable: true),
                    occurrence_count = table.Column<int>(type: "integer", nullable: false),
                    is_internal = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_crawl_edges", x => x.id);
                    table.ForeignKey(
                        name: "fk_crawl_edges_crawl_jobs_job_id",
                        column: x => x.job_id,
                        principalTable: "crawl_jobs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_crawl_edges_crawl_pages_source_page_id",
                        column: x => x.source_page_id,
                        principalTable: "crawl_pages",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_crawl_edges_crawl_pages_target_page_id",
                        column: x => x.target_page_id,
                        principalTable: "crawl_pages",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_crawl_edges_job_id_target_page_id",
                table: "crawl_edges",
                columns: new[] { "job_id", "target_page_id" });

            migrationBuilder.CreateIndex(
                name: "ix_crawl_edges_source_page_id",
                table: "crawl_edges",
                column: "source_page_id");

            migrationBuilder.CreateIndex(
                name: "ix_crawl_edges_target_page_id",
                table: "crawl_edges",
                column: "target_page_id");

            migrationBuilder.CreateIndex(
                name: "ux_crawl_edges_job_source_target",
                table: "crawl_edges",
                columns: new[] { "job_id", "source_page_id", "target_normalized_url" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_crawl_jobs_created_at_id",
                table: "crawl_jobs",
                columns: new[] { "created_at", "id" },
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "ix_crawl_jobs_status_lease_expires_at",
                table: "crawl_jobs",
                columns: new[] { "status", "lease_expires_at" });

            migrationBuilder.CreateIndex(
                name: "ix_crawl_pages_job_id_discovery_order",
                table: "crawl_pages",
                columns: new[] { "job_id", "discovery_order" });

            migrationBuilder.CreateIndex(
                name: "ix_crawl_pages_parent_page_id",
                table: "crawl_pages",
                column: "parent_page_id");

            migrationBuilder.CreateIndex(
                name: "ux_crawl_pages_job_id_normalized_url",
                table: "crawl_pages",
                columns: new[] { "job_id", "normalized_url" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_outbox_messages_unpublished",
                table: "outbox_messages",
                column: "created_at",
                filter: "published_at IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "crawl_edges");

            migrationBuilder.DropTable(
                name: "outbox_messages");

            migrationBuilder.DropTable(
                name: "crawl_pages");

            migrationBuilder.DropTable(
                name: "crawl_jobs");
        }
    }
}
