using SystemAutorun.App.Services;

namespace SystemAutorun.IntegrationTests;

public sealed class SingleInstanceServiceTests
{
    [Fact]
    public async Task ListenerSurvivesActivationCallbackFailure()
    {
        await using var service = CreateService();
        var firstActivation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondActivation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var activationCount = 0;
        service.StartListening(() =>
        {
            var current = Interlocked.Increment(ref activationCount);
            if (current == 1)
            {
                firstActivation.SetResult();
                throw new InvalidOperationException("Simulated activation failure.");
            }

            secondActivation.SetResult();
        });

        await service.NotifyPrimaryAsync();
        await firstActivation.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await service.NotifyPrimaryAsync();
        await secondActivation.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(2, activationCount);
    }

    [Fact]
    public async Task StartListeningIsIdempotent()
    {
        await using var service = CreateService();
        var activation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstCount = 0;
        var secondCount = 0;
        service.StartListening(() =>
        {
            Interlocked.Increment(ref firstCount);
            activation.SetResult();
        });
        service.StartListening(() => Interlocked.Increment(ref secondCount));

        await service.NotifyPrimaryAsync();
        await activation.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, firstCount);
        Assert.Equal(0, secondCount);
    }

    private static SingleInstanceService CreateService()
    {
        var instanceName = $"SystemAutorun.Tests.{Guid.NewGuid():N}";
        var service = new SingleInstanceService(instanceName);
        Assert.True(service.IsPrimary);
        return service;
    }
}
