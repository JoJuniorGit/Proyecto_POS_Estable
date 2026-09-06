using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Backend.API.Controllers;
using Core.Constants;
using Core.DTOs;
using Core.Entities;
using Core.Interfaces;
using Desktop.Client.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using Sales.Module.Data;
using Xunit;

namespace CommandCenter.Tests.Unit;

public class Phase4ResilienceRemediationTests
{
    private SalesDbContext CreateInMemorySalesDbContext()
    {
        var options = new DbContextOptionsBuilder<SalesDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        return new SalesDbContext(options);
    }

    [Fact]
    public void SecurityConstants_IsRootAdmin_IdentifiesRootAdminsCorrectly()
    {
        // Root Cedulas
        Assert.True(SecurityConstants.IsRootAdmin("V-00000000"));
        Assert.True(SecurityConstants.IsRootAdmin("v-00000000"));
        Assert.True(SecurityConstants.IsRootAdmin("V-12345678"));
        Assert.True(SecurityConstants.IsRootAdmin("v-12345678"));

        // Root Username
        Assert.True(SecurityConstants.IsRootAdmin(null, "Admin"));
        Assert.True(SecurityConstants.IsRootAdmin("", "admin"));

        // Non-Root
        Assert.False(SecurityConstants.IsRootAdmin("V-20111222", "cajero1"));
        Assert.False(SecurityConstants.IsRootAdmin(null, null));
    }

    [Theory]
    [InlineData("localhost", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("192.168.1.100", true)]
    [InlineData("10.0.0.5", true)]
    [InlineData("172.16.0.1", true)]
    [InlineData("172.31.255.254", true)]
    [InlineData("169.254.1.1", true)]
    [InlineData("8.8.8.8", false)]
    [InlineData("1.1.1.1", false)]
    [InlineData("example.com", false)]
    [InlineData("172.32.0.1", false)]
    [InlineData("", false)]
    public void SubnetScannerService_IsPrivateOrLocalAddress_ValidatesCorrectly(string host, bool expectedResult)
    {
        bool actual = SubnetScannerService.IsPrivateOrLocalAddress(host);
        Assert.Equal(expectedResult, actual);
    }

    [Fact]
    public async Task UsersController_UpdateUser_PreventsDeactivatingRootAdmin()
    {
        // Arrange
        using var db = CreateInMemorySalesDbContext();
        var rootAdmin = new User
        {
            Id = 1,
            Username = "Admin",
            Cedula = "V-00000000",
            Name = "Root Admin",
            Role = UserRole.Admin,
            IsActive = true
        };
        db.Users.Add(rootAdmin);
        await db.SaveChangesAsync();

        var controller = new UsersController(db, Mock.Of<IPasswordPolicyService>(), null);
        var httpContext = new DefaultHttpContext();
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, "2"),
            new Claim(ClaimTypes.Role, "Admin")
        };
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        var updateDto = new UpdateUserDto
        {
            Name = "Root Admin Modificado",
            Cedula = "V-00000000",
            Role = UserRole.Admin,
            IsActive = false // Intento de desactivar al root admin
        };

        // Act
        var result = await controller.UpdateUser(1, updateDto);

        // Assert
        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.NotNull(badRequest.Value);
    }
}
