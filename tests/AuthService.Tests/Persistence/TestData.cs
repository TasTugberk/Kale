using AuthService.Domain;
using AuthService.Persistence;

namespace AuthService.Tests.Persistence;

/// <summary>Small helpers that save one valid row each, so tests read as "given X, when Y".</summary>
internal static class TestData
{
    public static async Task<Application> AddApplication(this AuthDbContext db, string key)
    {
        var application = new Application { Key = key, Name = key };
        db.Applications.Add(application);
        await db.SaveChangesAsync();
        return application;
    }

    public static async Task<Operation> AddOperation(this AuthDbContext db, Application application, string member)
    {
        var operation = NewOperation(application, member);
        db.Operations.Add(operation);
        await db.SaveChangesAsync();
        return operation;
    }

    public static async Task<Role> AddRole(this AuthDbContext db, Application application, string name)
    {
        var role = new Role { ApplicationId = application.Id, Name = name };
        db.Roles.Add(role);
        await db.SaveChangesAsync();
        return role;
    }

    public static async Task<User> AddUser(this AuthDbContext db, string userName)
    {
        var user = new User { UserName = userName, DisplayName = userName };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    public static async Task<Group> AddGroup(this AuthDbContext db, string name)
    {
        var group = new Group { Name = name };
        db.Groups.Add(group);
        await db.SaveChangesAsync();
        return group;
    }

    public static Operation NewOperation(Application application, string member) => new()
    {
        ApplicationId = application.Id,
        ApplicationKey = application.Key,
        Name = $"{application.Key}.{member}",
    };
}
