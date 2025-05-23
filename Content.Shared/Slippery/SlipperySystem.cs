using Content.Shared.Administration.Logs;
using Content.Shared.Database;
using Content.Shared.Inventory;
using Robust.Shared.Network;
using Content.Shared.Popups;
using Content.Shared.StatusEffect;
using Content.Shared.StepTrigger.Systems;
using Content.Shared.Stunnable;
using Content.Shared.Throwing;
using Content.Shared.Mood;
using JetBrains.Annotations;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Containers;
using Robust.Shared.Network;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Events;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Utility;

namespace Content.Shared.Slippery;

[UsedImplicitly]
public sealed class SlipperySystem : EntitySystem
{
    [Dependency] private readonly INetManager _net = default!; // Goobstation
    [Dependency] private readonly ISharedAdminLogManager _adminLogger = default!;
    [Dependency] private readonly SharedAudioSystem _audio = default!;
    [Dependency] private readonly SharedStunSystem _stun = default!;
    [Dependency] private readonly StatusEffectsSystem _statusEffects = default!;
    [Dependency] private readonly SharedContainerSystem _container = default!;
    [Dependency] private readonly SharedPhysicsSystem _physics = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<SlipperyComponent, StepTriggerAttemptEvent>(HandleAttemptCollide);
        SubscribeLocalEvent<SlipperyComponent, StepTriggeredOffEvent>(HandleStepTrigger);
        SubscribeLocalEvent<NoSlipComponent, SlipAttemptEvent>(OnNoSlipAttempt);
        SubscribeLocalEvent<ThrownItemComponent, SlipCausingAttemptEvent>(OnThrownSlipAttempt);
        // as long as slip-resistant mice are never added, this should be fine (otherwise a mouse-hat will transfer it's power to the wearer).
        SubscribeLocalEvent<NoSlipComponent, InventoryRelayedEvent<SlipAttemptEvent>>((e, c, ev) => OnNoSlipAttempt(e, c, ev.Args));
        SubscribeLocalEvent<SlippableModifierComponent, SlippedEvent>(OnSlippableModifierSlipped);
    }

    private void HandleStepTrigger(EntityUid uid, SlipperyComponent component, ref StepTriggeredOffEvent args)
    {
        TrySlip(uid, component, args.Tripper);
    }

    private void HandleAttemptCollide(
        EntityUid uid,
        SlipperyComponent component,
        ref StepTriggerAttemptEvent args)
    {
        args.Continue |= component.SlipOnStep && CanSlip(uid, args.Tripper); // Goob edit
    }

    private static void OnNoSlipAttempt(EntityUid uid, NoSlipComponent component, SlipAttemptEvent args)
    {
        args.Cancel();
    }

    private void OnThrownSlipAttempt(EntityUid uid, ThrownItemComponent comp, ref SlipCausingAttemptEvent args)
    {
        args.Cancelled = true;
    }

    private void OnSlippableModifierSlipped(EntityUid uid, SlippableModifierComponent comp, ref SlippedEvent args)
    {
        args.ParalyzeTime *= comp.ParalyzeTimeMultiplier;
    }

    public bool CanSlip(EntityUid uid, EntityUid toSlip) // Goob edit
    {
        return !_container.IsEntityInContainer(uid)
                && _statusEffects.CanApplyEffect(toSlip, "KnockedDown", raiseEvent: false); // Goob edit
    }

    public void TrySlip(EntityUid uid, SlipperyComponent component, EntityUid other, bool requiresContact = true, bool force = false, bool predicted = true) // Goob edit
    {
        // Goob edit start
        if (!predicted && _net.IsClient)
            return;

        if ((HasComp<KnockedDownComponent>(other) || HasComp<StunnedComponent>(other)) && !component.SuperSlippery)
            return;

        if (!force)
        {
            var attemptEv = new SlipAttemptEvent();
            RaiseLocalEvent(other, attemptEv);
            if (attemptEv.Cancelled)
                return;

            var attemptCausingEv = new SlipCausingAttemptEvent();
            RaiseLocalEvent(uid, ref attemptCausingEv);
            if (attemptCausingEv.Cancelled)
                return;
        }

        var hardStun = false;
        // Goob edit end

        var ev = new SlipEvent(other);
        RaiseLocalEvent(uid, ref ev);

        var slippedEv = new SlippedEvent(uid, component.ParalyzeTime);
        RaiseLocalEvent(other, ref slippedEv);

        if (TryComp(other, out PhysicsComponent? physics) && !HasComp<SlidingComponent>(other))
        {
            _physics.SetLinearVelocity(other, physics.LinearVelocity * component.LaunchForwardsMultiplier, body: physics);

            if (component.SuperSlippery)
            {
                hardStun = true; // Goobstation
                var sliding = EnsureComp<SlidingComponent>(other);
                sliding.CollidingEntities.Add(uid);
                DebugTools.Assert(_physics.GetContactingEntities(other, physics).Contains(uid));
            }
        }

        var playSound = !_statusEffects.HasStatusEffect(other, "KnockedDown");

        // goob edit - stunmeta
        var time = TimeSpan.FromSeconds(component.ParalyzeTime);
        if (hardStun)
            _stun.TryParalyze(other, time, true);
        else
            _stun.TryKnockdown(other, time, true);
        RaiseLocalEvent(other, new MoodEffectEvent("MobSlipped"));

        // Preventing from playing the slip sound when you are already knocked down.
        if (playSound)
        {
            if (predicted)
                _audio.PlayPredicted(component.SlipSound, other, other);
            else
                _audio.PlayPvs(component.SlipSound, other);
        }

        _adminLogger.Add(LogType.Slip, LogImpact.Low,
            $"{ToPrettyString(other):mob} slipped on collision with {ToPrettyString(uid):entity}");
    }
}

/// <summary>
///     Raised on an entity to determine if it can slip or not.
/// </summary>
public sealed class SlipAttemptEvent : CancellableEntityEventArgs, IInventoryRelayEvent
{
    public SlotFlags TargetSlots { get; } = SlotFlags.FEET;
}

/// <summary>
/// Raised on an entity that is causing the slip event (e.g, the banana peel), to determine if the slip attempt should be cancelled.
/// </summary>
/// <param name="Cancelled">If the slip should be cancelled</param>
[ByRefEvent]
public record struct SlipCausingAttemptEvent (bool Cancelled);

/// Raised on an entity that CAUSED some other entity to slip (e.g., the banana peel).
/// <param name="Slipped">The entity being slipped</param>
[ByRefEvent]
public readonly record struct SlipEvent(EntityUid Slipped);

/// Raised on an entity that slipped.
/// <param name="Slipper">The entity that caused the slip</param>
/// <param name="ParalyzeTime">How many seconds the entity will be paralyzed for, modifiable with this event.</param>
[ByRefEvent]
public record struct SlippedEvent
{
    public readonly EntityUid Slipper;
    public float ParalyzeTime;

    public SlippedEvent(EntityUid slipper, float paralyzeTime)
    {
        Slipper = slipper;
        ParalyzeTime = paralyzeTime;
    }
}
