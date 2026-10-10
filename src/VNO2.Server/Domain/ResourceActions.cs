using System.Text.Json;
using VNO2.Contracts.Community;

namespace VNO2.Server.Domain;

public static class ResourceActions
{
    private const long MaximumCredits = 1_000_000_000;
    private const int MaximumQuantity = 1_000_000;

    public static object Apply(ActionContext context, string action, JsonElement data)
    {
        context.DemandEconomy();
        var account = context.Account;
        var state = context.State;
        if (action == "shop.configure")
        {
            context.DemandAdministrator();
            var id = data.Text("itemId", 80);
            if (!state.Items.ContainsKey(id) && state.Items.Count >= 1000) throw new DomainException("limit_exceeded", "Item catalog limit reached");
            state.Items[id] = new(id, data.Text("name", 100), data.Text("description", 1000, false), data.Money("price"),
                data.Number("hpEffect", -10000, 10000), data.Number("mpEffect", -10000, 10000),
                data.Number("cooldownSeconds", 0, 86400), data.Flag("consumable", true), data.Flag("forSale", true));
            context.PrivateEvent("shop.configured", new { itemId = id }, state.Accounts.Keys.ToArray());
            return state.Items[id];
        }
        if (action == "resources.set")
        {
            var targetId = data.Text("targetAccountId", 100, false);
            var target = targetId.Length == 0 ? account : context.Target(targetId);
            if (!WorldQueries.CanDirect(account) && (!context.Options.FreeformResourcesEnabled || target.Id != account.Id))
                throw new DomainException("forbidden", "Resource edits require a director or enabled self-edit permission");
            target.MaxHp = data.Number("maxHp", 1, 100000, target.MaxHp);
            target.MaxMp = data.Number("maxMp", 0, 100000, target.MaxMp);
            target.Hp = data.Number("hp", 0, target.MaxHp, Math.Min(target.Hp, target.MaxHp));
            target.Mp = data.Number("mp", 0, target.MaxMp, Math.Min(target.Mp, target.MaxMp));
            context.PrivateEvent("resources.changed", new { targetAccountId = target.Id, target.Hp, target.MaxHp, target.Mp, target.MaxMp }, target.Id, account.Id);
            return new { target.Hp, target.MaxHp, target.Mp, target.MaxMp };
        }
        if (action is "credits.transfer" or "credits.grant")
        {
            var target = context.Target(data.Text("targetAccountId", 100));
            var amount = data.Money("amount");
            if (amount <= 0) throw new DomainException("invalid_amount", "Amount must be positive");
            if (action == "credits.grant") context.DemandDirector();
            else
            {
                DemandTargetVisible(context, target);
                if (target.Id == account.Id) throw new DomainException("invalid_target", "Choose another player");
                if (account.Credits < amount) throw new DomainException("insufficient_credits", "Your balance is too low");
                account.Credits -= amount;
            }
            if (target.Credits > MaximumCredits - amount) throw new DomainException("balance_limit", "The recipient's balance would exceed the server limit");
            target.Credits += amount;
            context.PrivateEvent("credits.changed", new { sourceAccountId = account.Id, targetAccountId = target.Id, amount }, account.Id, target.Id);
            return new { amount, targetAccountId = target.Id };
        }
        var itemId = data.Text("itemId", 80);
        if (!state.Items.TryGetValue(itemId, out var item)) throw new DomainException("item_not_found", "The item does not exist");
        var quantity = data.Number("quantity", 1, 10000, 1);
        if (action == "shop.buy")
        {
            if (!item.ForSale) throw new DomainException("not_for_sale", "The item is not for sale");
            var price = checked(item.Price * quantity);
            if (account.Credits < price) throw new DomainException("insufficient_credits", "Your balance is too low");
            Add(account, itemId, quantity);
            account.Credits -= price;
            context.PrivateEvent("inventory.changed", new { itemId, quantity, paid = price }, account.Id);
            return new { itemId, quantity, paid = price };
        }
        if (action == "inventory.grant")
        {
            context.DemandDirector();
            var target = context.Target(data.Text("targetAccountId", 100));
            Add(target, itemId, quantity);
            context.PrivateEvent("inventory.changed", new { targetAccountId = target.Id, itemId, quantity }, target.Id, account.Id);
            return new { itemId, quantity };
        }
        if (account.Inventory.GetValueOrDefault(itemId) < quantity) throw new DomainException("insufficient_items", "You do not own that quantity");
        if (action == "inventory.show")
        {
            context.DemandSpeak();
            context.RoomEvent("inventory.shown", new { accountId = account.Id, itemId, item.Name, quantity });
            return new { shown = true };
        }
        if (action == "inventory.give")
        {
            var target = context.Target(data.Text("targetAccountId", 100));
            DemandTargetVisible(context, target);
            if (target.Id == account.Id) throw new DomainException("invalid_target", "Choose another player");
            Add(target, itemId, quantity);
            account.Inventory[itemId] -= quantity;
            context.PrivateEvent("inventory.changed", new { sourceAccountId = account.Id, targetAccountId = target.Id, itemId, quantity }, account.Id, target.Id);
            return new { itemId, quantity, targetAccountId = target.Id };
        }
        if (action == "inventory.discard")
        {
            account.Inventory[itemId] -= quantity;
            context.PrivateEvent("inventory.changed", new { itemId, discarded = quantity }, account.Id);
            return new { itemId, discarded = quantity };
        }
        if (quantity != 1) throw new DomainException("invalid_quantity", "Use one item at a time");
        var targetAccountId = data.Text("targetAccountId", 100, false);
        var receiver = targetAccountId.Length == 0 ? account : context.Target(targetAccountId);
        DemandTargetVisible(context, receiver);
        var now = DateTimeOffset.UtcNow;
        if (account.Cooldowns.TryGetValue(itemId, out var until) && until > now) throw new DomainException("cooldown", "This item is still on cooldown");
        receiver.Hp = Math.Clamp(receiver.Hp + item.HpEffect, 0, receiver.MaxHp);
        receiver.Mp = Math.Clamp(receiver.Mp + item.MpEffect, 0, receiver.MaxMp);
        if (item.Consumable) account.Inventory[itemId]--;
        var expiresAt = now.AddSeconds(item.CooldownSeconds);
        account.Cooldowns[itemId] = expiresAt;
        context.PrivateEvent("inventory.used", new { itemId, targetAccountId = receiver.Id, receiver.Hp, receiver.Mp, cooldownEndsAt = expiresAt }, account.Id, receiver.Id);
        return new { itemId, targetAccountId = receiver.Id, cooldownEndsAt = expiresAt };
    }

    private static void Add(AccountState account, string itemId, int quantity)
    {
        var current = account.Inventory.GetValueOrDefault(itemId);
        if (current > MaximumQuantity - quantity) throw new DomainException("inventory_limit", "The item quantity limit would be exceeded");
        account.Inventory[itemId] = current + quantity;
    }

    private static void DemandTargetVisible(ActionContext context, AccountState target)
    {
        context.DemandSpeak();
        if (target.Banned || context.Account.RoomId == null || context.Account.RoomId != target.RoomId ||
            !WorldQueries.CanSee(context.Account, target) || context.Account.Isolated && target.Id != context.Account.Id)
            throw new DomainException("invalid_target", "The target is unavailable in your room");
    }
}
