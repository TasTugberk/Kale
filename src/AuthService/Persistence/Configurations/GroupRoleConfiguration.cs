using AuthService.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AuthService.Persistence.Configurations;

internal sealed class GroupRoleConfiguration : IEntityTypeConfiguration<GroupRole>
{
    public void Configure(EntityTypeBuilder<GroupRole> builder)
    {
        builder.HasKey(gr => new { gr.GroupId, gr.RoleId });

        // An assignment means nothing without its group or role.
        builder.HasOne<Group>().WithMany().HasForeignKey(gr => gr.GroupId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Role>().WithMany().HasForeignKey(gr => gr.RoleId).OnDelete(DeleteBehavior.Cascade);
    }
}
