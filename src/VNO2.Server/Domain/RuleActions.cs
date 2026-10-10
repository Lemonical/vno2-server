using System.Security.Cryptography;
using System.Text.Json;

namespace VNO2.Server.Domain;

public static class RuleActions
{
    public static object Apply(ActionContext context, string action, JsonElement data)
    {
        context.DemandSpeak();
        if (action == "arithmetic.evaluate")
        {
            var expression = data.Text("expression", 200);
            var value = SafeArithmetic.Evaluate(expression);
            context.RoomEvent("arithmetic.result", new { accountId = context.Account.Id, expression, value });
            return new { expression, value };
        }
        var count = data.Number("count", 1, 100, 1);
        var sides = data.Number("sides", 2, 1000000, 20);
        var modifier = data.Number("modifier", -1000000, 1000000);
        var rolls = Enumerable.Range(0, count).Select(_ => RandomNumberGenerator.GetInt32(1, sides + 1)).ToArray();
        var total = rolls.Sum() + modifier;
        context.RoomEvent("dice.result", new { accountId = context.Account.Id, count, sides, modifier, rolls, total });
        return new { count, sides, modifier, rolls, total };
    }
}
