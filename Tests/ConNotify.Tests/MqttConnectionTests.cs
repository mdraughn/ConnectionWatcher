using ConNotify;

namespace ConNotify.Tests;

public class MqttConnectionTests
{
    [Fact]
    public void Options_carry_the_connect_timeout_and_credentials_only_with_a_user()
    {
        var withUser = MqttConnection.Options("broker", 1883, "u", "p").Build();
        Assert.Equal(TimeSpan.FromSeconds(5), withUser.Timeout);
        Assert.NotNull(withUser.Credentials);
        Assert.Null(MqttConnection.Options("broker", 1883, null, null).Build().Credentials);
    }
}
