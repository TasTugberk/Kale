using Microsoft.EntityFrameworkCore;

namespace AuthService.Persistence;

public sealed class AuthDbContext(DbContextOptions<AuthDbContext> options) : DbContext(options);
