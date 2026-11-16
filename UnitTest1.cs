
using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;

using BirdCentralServer.Controllers;
using BirdCentralServer.Data;
using BirdCentralServer.Models;
using BirdCentralServer.Services;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

using Xunit;

namespace BirdCentralServer.Tests;

public class CentralServerTests
{
    //тимчасова база для тесту
    private static AppDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        return new AppDbContext(options);
    }

    private static IConfiguration CreateConfig()
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AiServerUrl"] = "http://localhost:8001",
                ["RabbitMq:Host"] = "localhost"
            })
            .Build();
    }

    private static BirdsController CreateBirdsController(
        AppDbContext db)
    {
        var config = CreateConfig();

        var ai = new AiService(
            new HttpClient(),
            config
        );

        var rabbit = new RabbitMqService(config);

        return new BirdsController(db, ai, rabbit);
    }

    //  1 Порожня історія
    [Fact]
    public async Task Birds_EmptyHistory()
    {
        using var db = CreateDb();

        var controller = CreateBirdsController(db);

        var result = await controller.GetAll();

        var ok = Assert.IsType<OkObjectResult>(result);
        var birds = Assert.IsAssignableFrom<
            IEnumerable<BirdAnalysis>>(ok.Value);

        Assert.Empty(birds);
    }

    // 2 Історія сорт
    [Fact]
    public async Task Birds_HistoryOrder()
    {
        using var db = CreateDb();

        db.BirdAnalyses.Add(new BirdAnalysis
        {
            Id = 1,
            ImageName = "first.jpg",
            PredictedSpecies = "Sparrow"
        });

        db.BirdAnalyses.Add(new BirdAnalysis
        {
            Id = 2,
            ImageName = "second.jpg",
            PredictedSpecies = "Eagle"
        });

        await db.SaveChangesAsync();

        var controller = CreateBirdsController(db);

        var result = await controller.GetAll();

        var ok = Assert.IsType<OkObjectResult>(result);
        var birds = Assert.IsAssignableFrom<
            IEnumerable<BirdAnalysis>>(ok.Value).ToList();

        Assert.Equal(2, birds.Count);
        Assert.Equal("Eagle", birds[0].PredictedSpecies);
    }

    //  3Аналіз без файлу
    [Fact]
    public async Task Birds_WithoutFile()
    {
        using var db = CreateDb();

        var controller = CreateBirdsController(db);

        var result = await controller.Analyze(null!);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    // 4 Порожній файл
    [Fact]
    public async Task Birds_EmptyFile()
    {
        using var db = CreateDb();

        var controller = CreateBirdsController(db);

        using var stream = new MemoryStream();

        var file = new FormFile(
            stream, 0, 0, "file", "empty.jpg"
        );

        var result = await controller.Analyze(file);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    // 5-6адреса гаманця
    [Theory]
    [InlineData("wrong-wallet")]
    [InlineData("0x123")]
    public async Task Crypto_InvalidWallet(string wallet)
    {
        using var db = CreateDb();

        var controller = new CryptoController(db);

        var result = await controller.CheckWallet(wallet);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    // 7 Реєстрація гаманця
    [Fact]
    public async Task Crypto_RegisterWallet()
    {
        using var db = CreateDb();

        var controller = new CryptoController(db);

        var request = new CryptoRegisterRequest
        {
            WalletAddress = "0x1234567890123456789012345678901234567890",
            TransactionHash = "0xabc123"
        };

        var result = await controller.Register(request);

        Assert.IsType<OkObjectResult>(result);

        Assert.Single(db.CryptoUsers);
    }

    // 8Повторна реєстрація
    [Fact]
    public async Task Crypto_DuplicateWallet()
    {
        using var db = CreateDb();

        var controller = new CryptoController(db);

        var request = new CryptoRegisterRequest
        {
            WalletAddress = "0x1234567890123456789012345678901234567890",
            TransactionHash = "0xabc123"
        };

        await controller.Register(request);

        var secondResult = await controller.Register(request);

        Assert.IsType<BadRequestObjectResult>(secondResult);
        Assert.Single(db.CryptoUsers);
    }

    //HTTP-відповідь без справжAI Server
    private class FakeHttpHandler : HttpMessageHandler
    {
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

        public string ResponseJson { get; set; } =
            """
            {
              "image_name": "bird.jpg",
              "predictions": [
                {
                  "species": "Eagle",
                  "confidence": 0.95
                }
              ]
            }
            """;

        public string RequestBody { get; private set; } = "";

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestBody = request.Content == null
                ? ""
                : await request.Content.ReadAsStringAsync(
                    cancellationToken);

            return new HttpResponseMessage(Status)
            {
                Content = new StringContent(
                    ResponseJson,
                    Encoding.UTF8,
                    "application/json"
                )
            };
        }
    }

    private static IFormFile CreateImage(
        string contentType = "image/jpeg")
    {
        var bytes = Encoding.UTF8.GetBytes("fake image");

        var stream = new MemoryStream(bytes);

        return new FormFile(
            stream,
            0,
            bytes.Length,
            "file",
            "bird.jpg"
        )
        {
            Headers = new HeaderDictionary(),
            ContentType = contentType
        };
    }

    // 9 AI Ser повертає результат
    [Fact]
    public async Task Ai_Success()
    {
        var handler = new FakeHttpHandler();

        var service = new AiService(
            new HttpClient(handler),
            CreateConfig()
        );

        var result = await service.AnalyzeImage(CreateImage());

        Assert.NotNull(result);
        Assert.Single(result.Predictions);
    }

    // 10 Правильна назва птаха
    [Fact]
    public async Task Ai_CorrectSpecies()
    {
        var service = new AiService(
            new HttpClient(new FakeHttpHandler()),
            CreateConfig()
        );

        var result = await service.AnalyzeImage(CreateImage());

        Assert.NotNull(result);
        Assert.Equal("Eagle", result.Predictions[0].Species);
    }

    //11 Правильна впевненість AI
    [Fact]
    public async Task Ai_CorrectConfidence()
    {
        var service = new AiService(
            new HttpClient(new FakeHttpHandler()),
            CreateConfig()
        );

        var result = await service.AnalyzeImage(CreateImage());

        Assert.NotNull(result);
        Assert.Equal(0.95, result.Predictions[0].Confidence);
    }

    // 12 Порожній Content-Type замін на JPEG
    [Fact]
    public async Task Ai_EmptyContentType()
    {
        var handler = new FakeHttpHandler();

        var service = new AiService(
            new HttpClient(handler),
            CreateConfig()
        );

        var result = await service.AnalyzeImage(
            CreateImage("")
        );

        Assert.NotNull(result);
        Assert.Contains("image/jpeg", handler.RequestBody);
    }

    // 13 AI Server повернув помилку
    [Fact]
    public async Task Ai_ServerError()
    {
        var handler = new FakeHttpHandler
        {
            Status = HttpStatusCode.InternalServerError,
            ResponseJson = "Server error"
        };

        var service = new AiService(
            new HttpClient(handler),
            CreateConfig()
        );

        var result = await service.AnalyzeImage(CreateImage());

        Assert.Null(result);
    }
}
