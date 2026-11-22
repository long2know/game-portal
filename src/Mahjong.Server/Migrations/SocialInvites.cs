using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Mahjong.Server.Migrations;

[DbContext(typeof(MahjongDb))]
[Migration("20261122222500_SocialInvites")]
internal sealed class SocialInvites : Migration
{
    protected override void Up(MigrationBuilder migration)
    {
        migration.AddColumn<bool>("SocialAvailable", "Guests", nullable: false, defaultValue: false);
        migration.CreateTable("SocialInvites", table => new
        {
            Id = table.Column<string>(nullable: false),
            SenderId = table.Column<Guid>(nullable: false),
            RecipientId = table.Column<Guid>(nullable: false),
            Game = table.Column<string>(nullable: false),
            TableId = table.Column<string>(nullable: false),
            WaitingVersion = table.Column<long>(nullable: false),
            Accepted = table.Column<bool>(nullable: false),
            ExpiresAtTicks = table.Column<long>(nullable: false)
        }, constraints: table =>
        {
            table.PrimaryKey("PK_SocialInvites", row => row.Id);
            table.ForeignKey("FK_SocialInvites_Guests_SenderId", row => row.SenderId, "Guests", "Id",
                onDelete: ReferentialAction.Cascade);
            table.ForeignKey("FK_SocialInvites_Guests_RecipientId", row => row.RecipientId, "Guests", "Id",
                onDelete: ReferentialAction.Cascade);
        });
        migration.CreateIndex("IX_SocialInvites_ExpiresAtTicks", "SocialInvites", "ExpiresAtTicks");
        migration.CreateIndex("IX_SocialInvites_SenderId_Accepted_ExpiresAtTicks", "SocialInvites",
            new[] { "SenderId", "Accepted", "ExpiresAtTicks" });
        migration.CreateIndex("IX_SocialInvites_RecipientId_Accepted_ExpiresAtTicks", "SocialInvites",
            new[] { "RecipientId", "Accepted", "ExpiresAtTicks" });

        migration.CreateTable("SocialBlocks", table => new
        {
            BlockerId = table.Column<Guid>(nullable: false),
            BlockedId = table.Column<Guid>(nullable: false)
        }, constraints: table =>
        {
            table.PrimaryKey("PK_SocialBlocks", row => new { row.BlockerId, row.BlockedId });
            table.ForeignKey("FK_SocialBlocks_Guests_BlockerId", row => row.BlockerId, "Guests", "Id",
                onDelete: ReferentialAction.Cascade);
            table.ForeignKey("FK_SocialBlocks_Guests_BlockedId", row => row.BlockedId, "Guests", "Id",
                onDelete: ReferentialAction.Cascade);
        });
        migration.CreateIndex("IX_SocialBlocks_BlockedId", "SocialBlocks", "BlockedId");
    }

    protected override void Down(MigrationBuilder migration)
    {
        migration.DropTable("SocialInvites");
        migration.DropTable("SocialBlocks");
        migration.DropColumn("SocialAvailable", "Guests");
    }

    protected override void BuildTargetModel(ModelBuilder modelBuilder) => SocialSchema.Build(modelBuilder);
}

internal static class SocialSchema
{
    public static void Build(ModelBuilder builder)
    {
        SoloSchema.Build(builder);
        builder.Entity("Mahjong.Server.GuestRow")
            .Property<bool>("SocialAvailable").HasColumnType("INTEGER").HasDefaultValue(false);
        builder.Entity("Mahjong.Server.Social.SocialInviteRow", invite =>
        {
            invite.Property<string>("Id").HasColumnType("TEXT");
            invite.Property<Guid>("SenderId").HasColumnType("TEXT");
            invite.Property<Guid>("RecipientId").HasColumnType("TEXT");
            invite.Property<string>("Game").IsRequired().HasColumnType("TEXT");
            invite.Property<string>("TableId").IsRequired().HasColumnType("TEXT");
            invite.Property<long>("WaitingVersion").HasColumnType("INTEGER");
            invite.Property<bool>("Accepted").HasColumnType("INTEGER");
            invite.Property<long>("ExpiresAtTicks").HasColumnType("INTEGER");
            invite.HasKey("Id");
            invite.HasIndex("ExpiresAtTicks");
            invite.HasIndex("SenderId", "Accepted", "ExpiresAtTicks");
            invite.HasIndex("RecipientId", "Accepted", "ExpiresAtTicks");
            invite.HasOne("Mahjong.Server.GuestRow", null).WithMany()
                .HasForeignKey("SenderId").OnDelete(DeleteBehavior.Cascade).IsRequired();
            invite.HasOne("Mahjong.Server.GuestRow", null).WithMany()
                .HasForeignKey("RecipientId").OnDelete(DeleteBehavior.Cascade).IsRequired();
            invite.ToTable("SocialInvites");
        });
        builder.Entity("Mahjong.Server.Social.SocialBlockRow", block =>
        {
            block.Property<Guid>("BlockerId").HasColumnType("TEXT");
            block.Property<Guid>("BlockedId").HasColumnType("TEXT");
            block.HasKey("BlockerId", "BlockedId");
            block.HasIndex("BlockedId");
            block.HasOne("Mahjong.Server.GuestRow", null).WithMany()
                .HasForeignKey("BlockerId").OnDelete(DeleteBehavior.Cascade).IsRequired();
            block.HasOne("Mahjong.Server.GuestRow", null).WithMany()
                .HasForeignKey("BlockedId").OnDelete(DeleteBehavior.Cascade).IsRequired();
            block.ToTable("SocialBlocks");
        });
    }
}
