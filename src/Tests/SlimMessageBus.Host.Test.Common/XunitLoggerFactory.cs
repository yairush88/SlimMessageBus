﻿namespace SlimMessageBus.Host.Test.Common;

public class XunitLoggerFactory : ILoggerFactory
{
    private readonly ITestOutputHelper output;

    public XunitLoggerFactory(ITestOutputHelper output) => this.output = output;

    public void AddProvider(ILoggerProvider provider)
    {
    }

    public ILogger CreateLogger(string categoryName) => new XunitLogger(output, categoryName);

    public void Dispose()
    {
    }
}