﻿namespace SlimMessageBus.Host.Kafka.Test;

using SlimMessageBus.Host.Config;

public class KafkaProducerSettingsExtensionsTest
{
    [Fact]
    public void GivenDefaultWhenGetKeyProviderThenReturnsNull()
    {
        // arrange
        var ps = new ProducerSettings();

        // act
        var keyProvider = ps.GetKeyProvider();

        // assert
        keyProvider.Should().BeNull();
    }

    [Fact]
    public void GivenDefaultWhenGetPartitionProviderThenReturnsNull()
    {
        // arrange
        var ps = new ProducerSettings();

        // act
        var partitionProvider = ps.GetPartitionProvider();

        // assert
        partitionProvider.Should().BeNull();
    }
}
