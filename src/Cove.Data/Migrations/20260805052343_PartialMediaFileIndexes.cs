using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cove.Data.Migrations
{
    /// <inheritdoc />
    public partial class PartialMediaFileIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_files_AudioId_Path",
                table: "files");

            migrationBuilder.DropIndex(
                name: "IX_files_GalleryId_Path",
                table: "files");

            migrationBuilder.DropIndex(
                name: "IX_files_ImageId_Basename",
                table: "files");

            migrationBuilder.DropIndex(
                name: "IX_files_ImageId_Path",
                table: "files");

            migrationBuilder.DropIndex(
                name: "IX_files_TextDocumentId_Path",
                table: "files");

            migrationBuilder.DropIndex(
                name: "IX_files_VideoId_Path",
                table: "files");

            migrationBuilder.CreateIndex(
                name: "IX_files_AudioId_Path",
                table: "files",
                columns: new[] { "AudioId", "Path" },
                filter: "\"AudioId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_files_GalleryId_Path",
                table: "files",
                columns: new[] { "GalleryId", "Path" },
                filter: "\"GalleryId\" IS NOT NULL")
                .Annotation("Npgsql:IndexInclude", new[] { "Size", "ModTime" });

            migrationBuilder.CreateIndex(
                name: "IX_files_ImageId_Basename",
                table: "files",
                columns: new[] { "ImageId", "Basename" },
                filter: "\"ImageId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_files_ImageId_Path",
                table: "files",
                columns: new[] { "ImageId", "Path" },
                filter: "\"ImageId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_files_TextDocumentId_Path",
                table: "files",
                columns: new[] { "TextDocumentId", "Path" },
                filter: "\"TextDocumentId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_files_VideoId_Path",
                table: "files",
                columns: new[] { "VideoId", "Path" },
                filter: "\"VideoId\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_files_AudioId_Path",
                table: "files");

            migrationBuilder.DropIndex(
                name: "IX_files_GalleryId_Path",
                table: "files");

            migrationBuilder.DropIndex(
                name: "IX_files_ImageId_Basename",
                table: "files");

            migrationBuilder.DropIndex(
                name: "IX_files_ImageId_Path",
                table: "files");

            migrationBuilder.DropIndex(
                name: "IX_files_TextDocumentId_Path",
                table: "files");

            migrationBuilder.DropIndex(
                name: "IX_files_VideoId_Path",
                table: "files");

            migrationBuilder.CreateIndex(
                name: "IX_files_AudioId_Path",
                table: "files",
                columns: new[] { "AudioId", "Path" });

            migrationBuilder.CreateIndex(
                name: "IX_files_GalleryId_Path",
                table: "files",
                columns: new[] { "GalleryId", "Path" });

            migrationBuilder.CreateIndex(
                name: "IX_files_ImageId_Basename",
                table: "files",
                columns: new[] { "ImageId", "Basename" });

            migrationBuilder.CreateIndex(
                name: "IX_files_ImageId_Path",
                table: "files",
                columns: new[] { "ImageId", "Path" });

            migrationBuilder.CreateIndex(
                name: "IX_files_TextDocumentId_Path",
                table: "files",
                columns: new[] { "TextDocumentId", "Path" });

            migrationBuilder.CreateIndex(
                name: "IX_files_VideoId_Path",
                table: "files",
                columns: new[] { "VideoId", "Path" });
        }
    }
}
