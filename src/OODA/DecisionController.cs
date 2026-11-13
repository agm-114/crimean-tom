using System.Diagnostics;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SeaPower;
using SeaPower.GameBridge;

namespace OODA;

internal enum ControlMode { Manual, Observe, AiControl }

internal record DecisionEvent(double Time, string Stage, string Status, string Detail, string? ShipId, string? ShipName,
    string? RoeWarning, string? TargetId);

internal partial class DecisionController
{
    private record Request(DecisionRole Role, Task<JObject> Task, CancellationTokenSource Cancellation,
        Stopwatch Clock, ChartDestination[] Destinations, Dictionary<string, string> Routes, string? ShipId, bool ToolFollowUp = false);
    private record DeferredFire(JObject Order, string TargetId, double WallDeadline, double MissionDeadline);

    private readonly CodexDecisions codex = new();
    private readonly IDecisionProvider urgentProvider;
    private readonly DecisionClock clock = new();
    private readonly TimeInterrupts interrupts = new();
    private readonly TimeObservations timeObservations = new();
    private readonly AirDefenseAssessment airDefenseAssessment = new();
    private readonly AirDefenseRoutes airDefenseRoutes = new();
    private readonly SurfaceDefense defense = new();
    private readonly PlayerChart chart = new();
    private readonly FlightOrderChart flightOrderChart = new();
    private readonly Dictionary<string, FlightChartAssignment> flightAssignments = [];
    private FlightMapOrder[] flightMapOrders = [];
    private readonly Queue<DecisionEvent> events = new();
    private readonly Dictionary<string, DeferredFire> deferredFire = [];
    private Request? regular;
    private Request? urgent;
    private JObject? plan;
    private ChartMark[] marks = [];
    private ChartDestination? chartPoint;
    private string? mission;
    private string? previousSituation;
    private string? objectiveState;
    private readonly Dictionary<(string Sub, string Target), double> nextTorpedoAttempt = [];
    private readonly Dictionary<string, double> nextHelicopterEscape = [];
    private double nextTactical;
    private double nextTorpedoCheck;
    private double nextGroundOpportunity;
    private double nextOperational;
    private double nextQuietReview;
    private double nextAirWarningPreemption;
    private int urgentPreviews;
    private int receivedMessages;
    private bool lookupUsed;
    private bool replan;
    private bool reviewingFire;
    private bool wasAssessingAttack;
    private string? timedAttacker;
    private bool airDefensePending;
    private double raidWarningAt = double.NegativeInfinity;
    private readonly HashSet<(string Id, bool Nearby)> raidTracks = [];
    private readonly HashSet<string> loggedTimeSuggestions = new(StringComparer.OrdinalIgnoreCase);
    private string? lastTimeSuggestion;
    private string? statusMessage;

    internal DecisionController(IDecisionProvider? urgentProvider = null) => this.urgentProvider = urgentProvider ?? codex;

    internal ControlMode Mode { get; private set; }
    internal string? CameraUnitId { get; private set; }
    internal int Revision { get; private set; }
    internal string Objective => (string?)plan?["objective"] ?? "No plan yet";
    internal string Provider => $"Codex {CodexDecisions.Model} · urgent: {(urgentProvider == codex ? "Codex mock" : "OpenAI Decisions")}";
    internal PlanningThreadStatus[] PlanningThreads => codex.Threads();
    internal string Status => regular is { } request ? request.Task.IsCompleted ? $"{Stage(request.Role)} reply ready"
        : $"{Stage(request.Role)} pending · {request.Clock.Elapsed.TotalSeconds:0}s"
        : urgent is { } preview ? $"Urgent preview pending · {preview.Clock.Elapsed.TotalSeconds:0}s" : statusMessage ?? Mode switch
        { ControlMode.Observe => "Observing · orders are suggestions", ControlMode.AiControl => "AI control", _ => "Manual control" };
    internal JObject? LastInput { get; private set; }
    internal JObject? LastAirInput { get; private set; }
    internal JObject? LastUrgentInput { get; private set; }
    internal string? UrgentShip { get; private set; }
    internal bool CanPreviewUrgent => urgent == null && urgentPreviews < 6;
    internal string UrgentBudget => $"{urgentPreviews}/6 urgent previews used";
    internal string? Briefing { get; private set; }
    internal IReadOnlyCollection<DecisionEvent> Events => events;
    internal string ChartPoint => chartPoint?.Label ?? "No chart point added";
    internal bool ManageTime => clock.Enabled;
    internal string ClockStatus => clock.Status;
    internal (string Source, string Reason) ClockExplanation => clock.Explain(ManagingClock);
    internal JArray PlanMarks() => new(marks.Select(mark => mark.Describe()));
    internal JArray FlightMapOrders() => new(flightMapOrders.Select(order => order.Describe()));
    internal JArray AirDefenseRouteWarnings() => airDefenseRoutes.Read();
    internal string AirDefenseRouteSummary => airDefenseRoutes.Summary();
    internal JArray? ReadCoastalThreats() => PlayerPicture.Side is { } side
        ? defense.CoastalThreats(side, PlayerFlightDeck.Airborne(side)) : null;
    private bool AssessingAttack => defense.ActiveGunfire || defense.Pending || regular?.Role == DecisionRole.SurfaceDefense;
    private bool ManagingClock => Mode == ControlMode.AiControl && ManageTime;
    private (int Speed, string Source, string Reason)? DecisionPace => regular switch
    {
        { Role: DecisionRole.SurfaceDefense } request when request.Clock.Elapsed.TotalSeconds < 12 =>
            (2, "Surface defense", "Waiting briefly for a defensive fire decision."),
        { Role: DecisionRole.Operational or DecisionRole.RoutineTactical } request
            when request.Clock.Elapsed.TotalSeconds < 30 && airDefenseRoutes.HasImminentWarning =>
            (2, "Air-defense route", "A flight is approaching a known air-defense envelope while OODA reviews its route."),
        { Role: DecisionRole.Operational } request when request.Clock.Elapsed.TotalSeconds < 20
            && request.Destinations.Any(point => point.Id.StartsWith("screen-", StringComparison.Ordinal))
            && plan?["assignments"]?.Any(item => (string?)item["task"] == "Screen") != true =>
            (10, "Escort planning", "Waiting briefly for the first escort waypoint."),
        _ => null
    };

    internal void TickTime(double now)
    {
        if (mission == null || mission != (Globals.currentMissionFilePath ?? "") || PlayerPicture.Side is not { } side || !PlayerTime.Available) return;
        if (Mode == ControlMode.AiControl && !PlayerOrders.Blocked)
        {
            RetrySurfaceOrders(side);
            RunConditionalAttacks(side);
            RunConditionalOrders(side);
            UseGroundOpportunities(side);
        }
        if (Mode != ControlMode.Manual) PollOrderStatuses(side);
        interrupts.Expire(now);
        if (Mode != ControlMode.Manual)
        {
            SampleSurfaceMotion(side);
            PollAirDefenseRoutes(side, now);
            foreach (TimeEvent item in timeObservations.Poll(side)) ObserveTimeEvent(item, now);
            foreach (PlayerCombat.StrikeEffect effect in PlayerCombat.TakeStrikeEffects())
            {
                string detail = $"{effect.Weapon} hit {effect.Target}";
                if (effect.RadarChanges.Length > 0) detail += $" · {string.Join(", ", effect.RadarChanges.Select(change => change.Detail))}";
                Add("Strike assessment", effect.Bomb ? "Bomb impact" : "Radar damage observed", detail,
                    effect.UnitId, effect.Unit, targetId: effect.TargetId);
                nextTactical = 0;
                previousSituation = null;
            }
            bool assessingAttack = AssessingAttack;
            if (assessingAttack)
            {
                string? attackerId = PlayerCombat.Recent().Where(item => GameTime.missionElapsedTime - item.LastSeen <= 12)
                    .OrderByDescending(item => item.LastSeen).FirstOrDefault()?.AttackerId;
                if (!wasAssessingAttack || attackerId != null && attackerId != timedAttacker)
                {
                    Vehicle? track = attackerId == null ? null : PlayerPicture.LiveContact(side, attackerId);
                    ObserveTimeEvent(new(TimeEventKind.SurfaceAttack, attackerId,
                        track == null ? "Surface attack" : PlayerPicture.ContactLabel(track), "Surface",
                        track?.CurrentRelationship().ToString() ?? "Unknown",
                        attackerId == null ? "Surface threat under review" : "Gunfire observed"), now);
                }
                if (attackerId != null) timedAttacker = attackerId;
            }
            else timedAttacker = null;
            wasAssessingAttack = assessingAttack;
        }
        else
        {
            PlayerCombat.TakeMissileImpacts();
            PlayerCombat.TakeStrikeEffects();
        }
        clock.Tick(side, Mode == ControlMode.AiControl, plan != null, interrupts.Current, DecisionPace);
    }

    private void PollAirDefenseRoutes(Taskforce side, double now)
    {
        AirDefenseRoutes.Warning[] warnings = airDefenseRoutes.Poll(side, airDefenseAssessment);
        if (warnings.Length == 0) return;
        foreach (AirDefenseRoutes.Warning warning in warnings)
        {
            Add("Air operations", "Air-defense route warning", warning.Detail, warning.UnitId, warning.UnitName, targetId: warning.TargetId);
            ObserveTimeEvent(new(TimeEventKind.AirDefenseRoute, warning.UnitId, warning.UnitName, "Air", "Own", warning.Detail), now);
        }
        if (Mode == ControlMode.AiControl && !PlayerOrders.Blocked)
            EvadeHelicopterThreats(side, warnings);
        nextTactical = 0;
        previousSituation = null;
        if (now < nextAirWarningPreemption || !warnings.Any(warning => warning.Imminent && !warning.Suppressed)
            || regular is not { Role: DecisionRole.Operational or DecisionRole.RoutineTactical, Task.IsCompleted: false } request
            || request.Clock.Elapsed.TotalSeconds >= 8) return;
        if (request.Role == DecisionRole.Operational)
        {
            replan = true;
            nextOperational = 0;
        }
        nextAirWarningPreemption = now + 30;
        Cancel(ref regular);
    }

    private void EvadeHelicopterThreats(Taskforce side, AirDefenseRoutes.Warning[] warnings)
    {
        var threats = warnings.Where(warning => warning.Imminent && !warning.Suppressed && warning.Basis != "Direct attack approach")
            .GroupBy(warning => warning.UnitId).ToDictionary(group => group.Key, group => group.First());
        if (threats.Count == 0) return;
        double now = GameTime.missionElapsedTime;
        foreach (Helicopter helo in PlayerPicture.Units(side).OfType<Helicopter>())
        {
            string id = PlayerPicture.Id(helo);
            if (!threats.TryGetValue(id, out AirDefenseRoutes.Warning? warning) || now < nextHelicopterEscape.GetValueOrDefault(id)
                || !PlayerFlightDeck.CanEvadeInHelicopter(helo)) continue;
            GeoPosition[] route = airDefenseRoutes.EscapeRoute(side, airDefenseAssessment, helo);
            if (route.Length == 0) continue;
            string status = PlayerFlightDeck.EvadeInHelicopter(side, helo, route);
            nextHelicopterEscape[id] = now + 30;
            Add("Air operations", status, $"Automatic escape from {warning.TargetName}'s envelope", warning.UnitId, warning.UnitName,
                targetId: warning.TargetId);
            RecordFlightAssignment(warning.UnitId, "Disengage", null, status);
        }
    }

    internal void ApplyReadyDecision()
    {
        if (Mode == ControlMode.Manual || regular is not { Task.IsCompleted: true } work
            || PlayerPicture.Side is not { } side || !Singleton<MissionManager>.InstanceExists()
            || Singleton<MissionManager>.Instance.MissionEnded || mission != (Globals.currentMissionFilePath ?? "")) return;
        ReadCommunications();
        if (regular != work || !reviewingFire && work.Role != DecisionRole.AirDefense
            && (airDefensePending || defense.Pending && work.Role != DecisionRole.SurfaceDefense)) return;
        if (work.Task.Status == TaskStatus.RanToCompletion && Mode == ControlMode.AiControl)
        {
            clock.Resume();
            if (PlayerOrders.Blocked) return;
        }
        regular = null;
        using CancellationTokenSource cancellation = work.Cancellation;
        try { Apply(work, work.Task.GetAwaiter().GetResult(), side); }
        catch (Exception error)
        {
            statusMessage = cancellation.IsCancellationRequested ? "Codex request timed out" : error.Message;
            if (cancellation.IsCancellationRequested) codex.OnDecisionTimeout(work.Role);
            Add(Stage(work.Role), "Failed", statusMessage);
            previousSituation = null;
            if (work.Role == DecisionRole.Operational)
            {
                reviewingFire = false;
                replan = true;
                nextOperational = GameTime.missionElapsedTime + 60;
            }
        }
        if (regular == null) clock.Resume();
    }

    private void ObserveTimeEvent(TimeEvent item, double now)
    {
        if (interrupts.Observe(item, now, ManagingClock) is { } rule)
            Add("Time control", $"Interrupt · {rule.Speed}× for {rule.HoldSeconds}s", item.Detail, item.SubjectId, item.Label);
        if (item.Kind == TimeEventKind.MissileHit && (defense.ActiveGunfire || defense.HasNearbyThreat)) defense.Pending = true;
        if (item.Posture == "Hostile" && item.Category is "Surface" or "Radar" or "SAM"
            && (item.Kind is TimeEventKind.TrackAppeared or TimeEventKind.HostileNearby
                || item.Kind == TimeEventKind.TrackChanged && item.PreviousPosture != item.Posture))
        {
            nextTactical = 0;
            previousSituation = null;
        }
        if (item.Kind is TimeEventKind.HostileNearby or TimeEventKind.TrackAppeared or TimeEventKind.RadarContact or TimeEventKind.TrackChanged
            && item.Category is "Air" or "Helicopter" && item.Posture == "Hostile" && item.SubjectId is { } id
            && GameTime.missionElapsedTime - raidWarningAt <= 900 && raidTracks.Add((id, item.Kind == TimeEventKind.HostileNearby)))
        {
            airDefensePending = true;
            TimeEvent raidContact = item with { Kind = TimeEventKind.AirRaidWarning, Detail = "Hostile aircraft tracked after the raid warning" };
            if (interrupts.Observe(raidContact, now, ManagingClock) is { } raidRule)
                Add("Time control", $"Interrupt · {raidRule.Speed}× for {raidRule.HoldSeconds}s", raidContact.Detail, item.SubjectId, item.Label);
        }
    }

    internal void SetTimeManagement(bool enabled)
    {
        clock.Enabled = enabled;
        if (!enabled) interrupts.CancelActive();
        clock.Tick(PlayerPicture.Side, Mode == ControlMode.AiControl, plan != null, interrupts.Current);
    }

    internal void SetMode(ControlMode mode)
    {
        if (mode != ControlMode.Manual && PlayerPicture.Side == null) return;
        if (mode == Mode) return;
        Cancel(ref regular);
        Cancel(ref urgent);
        Mode = mode;
        if (mode == ControlMode.Manual) codex.Reset();
        deferredFire.Clear();
        sensorSweeps.CancelAll();
        foreach (ConditionalAttack order in conditionalAttacks.Values) FinishConditionalAttack(order, "superseded", "AI control ended");
        conditionalAttacks.Clear();
        CancelConditionalOrders();
        wasAssessingAttack = false;
        timedAttacker = null;
        if (mode != ControlMode.AiControl)
        {
            chart.Clear();
            flightOrderChart.Clear();
            flightMapOrders = [];
            flightAssignments.Clear();
            interrupts.CancelActive();
        }
        if (mode == ControlMode.Manual)
        {
            timeObservations.Reset();
            airDefenseRoutes.Reset();
        }
        clock.Tick(PlayerPicture.Side, mode == ControlMode.AiControl, plan != null, interrupts.Current);
        statusMessage = null;
        replan = mode != ControlMode.Manual;
        Add("Control", "Changed", mode.ToString());
    }

    internal void Reset()
    {
        Cancel(ref regular);
        Cancel(ref urgent);
        codex.Reset();
        sensorSweeps.CancelAll();
        mission = null;
        Mode = ControlMode.Manual;
        CameraUnitId = null;
        clock.Reset();
        interrupts.Reset();
        timeObservations.Reset();
        airDefenseAssessment.Reset();
        airDefenseRoutes.Reset();
        defense.Reset();
        deferredFire.Clear();
        conditionalAttacks.Clear();
        conditionalOrders.Clear();
        orderStatuses.Clear();
        nextOrderId = 0;
        nextOrderStatusCheck = nextConditionalOrderCheck = nextSurfaceSample = 0;
        chart.Clear();
        flightOrderChart.Clear();
        flightMapOrders = [];
        flightAssignments.Clear();
        marks = [];
        plan = LastInput = LastAirInput = null;
        LastUrgentInput = null;
        UrgentShip = null;
        chartPoint = null;
        Briefing = previousSituation = objectiveState = statusMessage = null;
        nextTorpedoAttempt.Clear();
        nextHelicopterEscape.Clear();
        nextTactical = nextOperational = urgentPreviews = 0;
        nextTorpedoCheck = 0;
        nextGroundOpportunity = 0;
        nextConditionalAttackCheck = nextHailCheck = 0;
        hailedTracks.Clear();
        nextQuietReview = 0;
        nextAirWarningPreemption = 0;
        receivedMessages = 0;
        lookupUsed = replan = reviewingFire = false;
        wasAssessingAttack = false;
        timedAttacker = null;
        airDefensePending = false;
        raidWarningAt = double.NegativeInfinity;
        raidTracks.Clear();
        loggedTimeSuggestions.Clear();
        loggedCapabilityRequests.Clear();
        lastTimeSuggestion = null;
        events.Clear();
        Revision++;
    }

    internal void PlanNow()
    {
        if (Mode == ControlMode.Manual || regular != null) return;
        replan = true;
        nextOperational = 0;
        statusMessage = null;
        Add("Mission Command", "Requested", "Replan requested from the decision window");
    }

    internal void SetChartPoint(string label, double latitude, double longitude)
    {
        if (PlayerPicture.Side == null || !double.IsFinite(latitude) || !double.IsFinite(longitude)
            || Math.Abs(latitude) > 90 || Math.Abs(longitude) > 180) return;
        chartPoint = new("chart-point", string.IsNullOrWhiteSpace(label) ? "Player chart point" : label.Trim(),
            new GeoPosition { _latitude = latitude, _longitude = longitude });
        Cancel(ref regular);
        nextOperational = 0;
        replan = Mode != ControlMode.Manual;
        Add("Navigation", "Chart point added", $"{chartPoint.Label} · {latitude:0.0000}, {longitude:0.0000}");
    }

    internal void Tick()
    {
        Taskforce? side = PlayerPicture.Side;
        if (side != null && Mode == ControlMode.AiControl && MissionCommunications.Acknowledge() is { } popup)
        {
            clock.Resume();
            ReadCommunications();
            string title = popup.Title.Length > 0 ? popup.Title : "Mission message";
            Add("Mission popup", "Acknowledged", popup.Button.Length > 0 ? $"{title} · {popup.Button}" : title);
            return;
        }
        if (side == null || !Singleton<MissionManager>.InstanceExists() || Singleton<MissionManager>.Instance.MissionEnded)
        {
            if (Mode != ControlMode.Manual) SetMode(ControlMode.Manual);
            clock.Tick(side, controlling: false);
            return;
        }
        string currentMission = Globals.currentMissionFilePath ?? "";
        if (mission != currentMission)
        {
            Reset();
            mission = currentMission;
            Briefing = PlayerBriefing.Read();
            SetMode(ControlMode.AiControl);
        }
        ReadCommunications();
        if (Mode != ControlMode.Manual)
        {
            defense.Poll(side, regular?.Role != DecisionRole.SurfaceDefense);
            if (Mode == ControlMode.AiControl && !PlayerOrders.Blocked) UseTorpedoWindows(side);
            foreach (var (id, label, assessment) in airDefenseAssessment.Poll(side))
            {
                Add("Battle damage", assessment, label, targetId: id);
                nextTactical = 0;
                previousSituation = null;
            }
            if (Mode == ControlMode.AiControl && !PlayerOrders.Blocked)
            {
                PlayerFlightDeck.UpdateAirborneOrders(side, ReportDepartureOrder);
                HailSurfaceContacts(side);
                foreach (PlayerFlightDeck.AirDepartureResult flight in PlayerFlightDeck.RecoverSpentCap(side))
                {
                    Add("Air operations", flight.Status, "CAP · No air-to-air missiles remaining. Recover to rearm.", flight.UnitId, flight.Name);
                    flightAssignments.Remove(flight.UnitId);
                    nextTactical = 0;
                    previousSituation = null;
                }
                RespondToObservedGunfire(side);
            }
            if (Mode == ControlMode.AiControl)
            {
                flightMapOrders = PlayerFlightDeck.MapOrders(side, flightAssignments);
                flightOrderChart.Update(flightMapOrders);
            }
        }
        if (!reviewingFire && regular is { Role: not DecisionRole.AirDefense } interrupted
            && (airDefensePending || defense.Pending && interrupted.Role != DecisionRole.SurfaceDefense))
        {
            if (interrupted.Role == DecisionRole.Operational) { replan = true; nextOperational = 0; }
            if (interrupted.Role == DecisionRole.SurfaceDefense) defense.Pending = true;
            Cancel(ref regular);
        }
        clock.Tick(side, Mode == ControlMode.AiControl, plan != null, interrupts.Current, DecisionPace);
        CompleteUrgent();
        if (Mode == ControlMode.Manual) return;
        if (regular != null) return;
        clock.Resume();
        if (PlayerTime.Blocked || GameTime.RadioSequenceForcesNormalTime) return;

        if (!reviewingFire && airDefensePending)
        {
            airDefensePending = false;
            Vessel[] escorts = PlayerPicture.Ships(side);
            Start(DecisionRole.AirDefense, PlayerPicture.Read(side, escorts), escorts, []);
            return;
        }

        if (!reviewingFire && defense.Pending)
        {
            defense.MarkRequested();
            Vessel[] responders = PlayerPicture.Ships(side);
            Start(DecisionRole.SurfaceDefense, PlayerPicture.Read(side, responders), responders, []);
            return;
        }

        double now = GameTime.missionElapsedTime;
        if (!replan && now < nextTactical) return;
        if (replan && now < nextOperational) return;
        Vessel[] ships = PlayerPicture.Ships(side);
        JObject view = PlayerPicture.Read(side, ships);
        string currentObjectives = view["objectiveUpdates"]!.ToString(Formatting.None);
        if (objectiveState != null && objectiveState != currentObjectives) replan = true;
        objectiveState = currentObjectives;
        if (replan && now < nextOperational) return;
        if (!replan && plan == null) return;

        ReadAirPicture(view, side);
        string situation = Signature(view);
        nextTactical = now + 60;
        double wallTime = UnityEngine.Time.realtimeSinceStartupAsDouble;
        if (!replan && situation == previousSituation)
        {
            if (!ManagingClock || wallTime < nextQuietReview) return;
            float gameLimit = PlayerTime.Maximum;
            if (!DecisionClock.Speeds.Any(speed => speed > clock.CruiseSpeed && speed <= gameLimit)) return;
        }
        nextQuietReview = wallTime + 30;
        previousSituation = situation;
        DecisionRole role = replan ? DecisionRole.Operational : DecisionRole.RoutineTactical;
        replan = false;
        if (role == DecisionRole.Operational) { lookupUsed = false; nextOperational = now + 60; }
        Start(role, view, ships, NavigationOrders.Destinations(side, ships, chartPoint));
    }

    private void ReadCommunications()
    {
        IReadOnlyList<MissionCommunications.Message> messages = MissionCommunications.Messages;
        if (receivedMessages == messages.Count) return;
        var updates = messages.Skip(receivedMessages).ToArray();
        if (updates.Any(message => message.Kind == "Mission popup")) clock.YieldPause();
        receivedMessages = messages.Count;
        Revision++;
        if (Mode == ControlMode.Manual) return;
        if (updates.LastOrDefault(message => message.WarnsOfAirRaid) is { } warning)
        {
            raidWarningAt = warning.Time;
            raidTracks.Clear();
            airDefensePending = true;
            ObserveTimeEvent(new(TimeEventKind.AirRaidWarning, null, "Inbound strike", "Air", "Hostile", "Raid warning received"),
                UnityEngine.Time.realtimeSinceStartupAsDouble);
        }
        defense.OnMessages(updates);
        if (regular?.Role is not (DecisionRole.AirDefense or DecisionRole.SurfaceDefense)) Cancel(ref regular);
        replan = true;
        nextOperational = 0;
    }

    internal void PreviewUrgent(string shipId)
    {
        if (urgent != null || urgentPreviews >= 6 || PlayerPicture.Side is not { } side) return;
        Vessel? ship = PlayerPicture.Ships(side).FirstOrDefault(item => PlayerPicture.Id(item) == shipId);
        if (ship == null) return;
        JObject view = new()
        {
            ["scope"] = "player", ["unit"] = new JObject { ["name"] = ship.getName(), ["exercise"] = "Synthetic last-salvo preview",
                ["damage"] = "Severe flooding; one launcher remains available", ["defense"] = "Close-in defense remains active" },
            ["threat"] = "Observed inbound salvo; in this exercise an identified authorized hostile surface target is in range, with no friendly salvo already inbound.",
            ["deadline"] = "Preview only · no live orders", ["plan"] = "Protect the screened force.", ["missionMessages"] = MissionCommunications.Snapshot(),
            ["options"] = new JArray(new JObject { ["id"] = "hold", ["description"] = "Keep defending and hold the remaining missiles" },
                new JObject { ["id"] = "limited-salvo", ["description"] = "Use the remaining ready launcher for one limited salvo at the authorized target" })
        };
        DecisionPrompt prompt = DecisionPrompt.ForMission(DecisionRole.UrgentTactical, view);
        LastUrgentInput = prompt.Input;
        UrgentShip = shipId;
        CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(45));
        urgent = new(DecisionRole.UrgentTactical, urgentProvider.Decide(DecisionRole.UrgentTactical, prompt, cancellation.Token),
            cancellation, Stopwatch.StartNew(), [], new(), shipId);
        urgentPreviews++;
        Add("Urgent preview", "Requested", $"Synthetic last-salvo choice · {Provider}", shipId);
    }

    internal JObject Report() => new()
    {
        ["mode"] = Mode.ToString(), ["status"] = Status, ["provider"] = Provider, ["urgentPreviews"] = urgentPreviews,
        ["planningThreads"] = new JArray(PlanningThreads.Select(thread => new JObject
        {
            ["id"] = thread.Id, ["role"] = thread.Role.ToString(), ["state"] = thread.State.ToString(),
            ["decisions"] = thread.Decisions, ["compactions"] = thread.Compactions, ["lastInputTokens"] = thread.LastInputTokens,
            ["error"] = thread.Error
        })),
        ["chartPoint"] = chartPoint?.Label, ["briefing"] = Briefing,
        ["plan"] = plan?.DeepClone(), ["lastInput"] = LastInput?.DeepClone(), ["missionMessages"] = MissionCommunications.Snapshot(),
        ["planMarks"] = PlanMarks(), ["flightMapOrders"] = FlightMapOrders(), ["airDefenseAssessment"] = airDefenseAssessment.Read(),
        ["airDefenseRouteWarnings"] = AirDefenseRouteWarnings(),
        ["conditionalAttacks"] = ReadConditionalAttacks(),
        ["conditionalOrders"] = ReadConditionalOrders(), ["orderStatuses"] = ReadOrderStatuses(40),
        ["sensorSweeps"] = sensorSweeps.Read(),
        ["identificationRequests"] = ReadIdentificationRequests(),
        ["time"] = new JObject { ["managed"] = ManagingClock, ["status"] = ClockStatus,
            ["source"] = ClockExplanation.Source, ["reason"] = ClockExplanation.Reason, ["cruiseSpeed"] = clock.CruiseSpeed,
            ["cruiseReason"] = clock.CruiseReason, ["interrupts"] = interrupts.Describe() },
        ["lastUrgentInput"] = LastUrgentInput?.DeepClone(), ["urgentShipId"] = UrgentShip,
        ["events"] = new JArray(events.Select(item => new JObject { ["time"] = item.Time, ["stage"] = item.Stage,
            ["status"] = item.Status, ["detail"] = item.Detail, ["shipId"] = item.ShipId, ["shipName"] = item.ShipName,
            ["roeWarning"] = item.RoeWarning, ["targetId"] = item.TargetId }))
    };

    internal string Assignment(string shipId) => plan?["assignments"]?.OfType<JObject>()
        .FirstOrDefault(item => (string?)item["groupId"] == shipId) is { } assignment
            ? $"{(string?)assignment["task"]} · {(string?)assignment["reason"]}" : "No independent assignment";

    private void Start(DecisionRole role, JObject view, Vessel[] ships, ChartDestination[] destinations, bool toolFollowUp = false)
    {
        view["plan"] = plan?.DeepClone();
        view["missionMessages"] = MissionCommunications.Snapshot();
        if (role is DecisionRole.Operational or DecisionRole.RoutineTactical)
        {
            if (PlayerPicture.Side is { } planningSide)
            {
                if (view["airDecks"] == null) ReadAirPicture(view, planningSide);
                ReadOrderTools(view, planningSide);
                view["maritimeShips"] = PlayerOrders.SurfaceStrikeOptions(PlayerPicture.Units(planningSide).Where(unit => unit is Vessel or Submarine));
                view["helicopterDecks"] = PlayerFlightDeck.Helicopters(planningSide, ships);
                view["helicopterScreenAnchors"] = new JArray(SurfaceDefense.ProtectedContacts(planningSide).Select(track => new JObject
                {
                    ["id"] = PlayerPicture.Id(track), ["name"] = PlayerPicture.ContactLabel(track),
                    ["position"] = PlayerPicture.Point(track.PositionEstimate!.Value.Item1)
                }));
            }
            JObject time = interrupts.Describe();
            time["allowedSpeeds"] = new JArray(ManagingClock
                ? DecisionClock.Speeds.Where(speed => speed <= PlayerTime.Maximum) : Enumerable.Empty<int>());
            time["defaultCruiseSpeed"] = DecisionClock.DefaultCruiseSpeed;
            time["cruiseSpeed"] = clock.CruiseSpeed;
            time["cruiseReason"] = clock.CruiseReason;
            time["currentSource"] = ClockExplanation.Source;
            time["currentReason"] = ClockExplanation.Reason;
            time["lastDefaultSuggestion"] = lastTimeSuggestion;
            time["recentSpeedChanges"] = new JArray(events.Where(item => item.Stage == "Time control").Reverse().Take(8)
                .Select(item => new JObject { ["time"] = item.Time, ["change"] = item.Status, ["reason"] = item.Detail,
                    ["subject"] = item.ShipName }));
            view["timeInterrupts"] = time;
        }
        if (PlayerPicture.Side is { } defensiveSide)
        {
            defense.Describe(defensiveSide, view);
            if (role is DecisionRole.SurfaceDefense or DecisionRole.AirDefense)
                view["engagementOptions"] = NearbyEngagementOptions(defensiveSide, PlayerPicture.Units(defensiveSide), 4, airOnly: role == DecisionRole.AirDefense);
        }
        if (role != DecisionRole.SurfaceDefense && PlayerPicture.Side is { } side)
        {
            view["standingOrders"] = PlayerDoctrine.Read(side);
            view["availableTimeCompressions"] = new JArray(Mode == ControlMode.AiControl && ManageTime
                ? DecisionClock.Options(side) : Enumerable.Empty<int>());
        }
        view["allowedTasks"] = new JArray("Transit", "Screen", "Hold");
        view["allowedActions"] = new JArray("SetWaypoint", "Wait", "RequestReplan");
        view["destinations"] = new JArray(destinations.Select(destination => new JObject
        {
            ["id"] = destination.Id, ["label"] = destination.Label, ["position"] = PlayerPicture.Point(destination.Position),
            ["groupId"] = destination.GroupId
        }));
        JArray tools = new();
        if (role == DecisionRole.Operational && !lookupUsed && !toolFollowUp) tools.Add(new JObject
        {
            ["name"] = "get_owned_unit", ["requestField"] = "inspectGroupId", ["description"] = "Set inspectGroupId to an owned group ID for its heading and extended route, or null to submit the plan."
        });
        if (role is DecisionRole.Operational or DecisionRole.RoutineTactical)
        {
            Offer("set_surface_route", "surfaceRoutes", "Move a ship or formation leader along chart coordinates.");
            Offer("stage_submarine", "approaches", "Stage an owned submarine near torpedo range of a listed surface contact.");
            Offer("position_surface_ship", "approaches", "Move a ship to a standoff or shore gun position.");
            Offer("engage_or_bombard", "maritimeOrders", "Assign ships or submarines to engage a live hostile contact.");
            Offer("manage_flights", "airOrders", "Prepare, launch, route, hold, disengage, strike, recover, split, or join aircraft.");
            Offer("launch_helicopter_screen", "helicopterOrders", "Launch an escort helicopter to screen a defended contact or the formation.");
            Offer("set_time_policy", "timePolicy", "Set cruise speed and time interrupts.");
            Offer("attack_when_track_live", "conditionalAttacks", "Arm or cancel a one-shot attack on a plotted target when it has a live hostile fix.");
            Offer("set_movement_profile", "movementProfiles", "Set supported native speed, altitude, depth, or active sensor posture.");
            Offer("arm_conditional_order", "conditionalOrders", "Arm or cancel a one-shot movement or relief order on a player-visible condition.");
            Offer("request_capability", "capabilityRequests", "Log a concrete request for a new automation, native tool, or analysis function for the developers.");
            Offer("sweep_sensor", "sensorSweeps", "Take one bounded radar scan, passive ESM listen, or visual periscope look.");
            if (!toolFollowUp)
            {
                Offer("preview_route", "decisionQueries", "Estimate travel time, unsafe leg, exposure and arrival endurance.");
                Offer("preview_sortie_cycle", "decisionQueries", "Estimate a fit's time to station and full sortie cycle from a carrier or airbase.");
                Offer("get_engagement_options", "decisionQueries", "Compare compatible weapons, release estimates, and blockers for a plotted contact.");
                Offer("get_order_status", "decisionQueries", "Check progress and the latest native result for an order ID.");
                Offer("estimate_damage", "decisionQueries", "Estimate direct hits needed against a known ground or naval class.");
                Offer("estimate_detection", "decisionQueries", "Estimate radar or sonar range using Sea Power Encyclopedia models.");
            }
            view["conditionalAttacks"] = ReadConditionalAttacks();
            view["identificationRequests"] = ReadIdentificationRequests();
        }
        if (role is DecisionRole.RoutineTactical or DecisionRole.AirDefense)
            Offer("set_standing_orders", "standingOrderChanges", "Change available native Standing Orders.");
        if (role == DecisionRole.RoutineTactical) Offer("move_assigned_group", "orders", "Apply an assigned waypoint or request a replan.");
        if (role == DecisionRole.Operational) Offer("mark_map", "mapMarks", "Mark objectives, reference points or search areas.");
        if (role == DecisionRole.SurfaceDefense) Offer("defensive_fire", "engagements", "Choose ship, weapon and evidence for defensive fire.");
        if (role == DecisionRole.AirDefense) Offer("intercept_air_tracks", "targetIds", "Select hostile raid contacts for ship air defense.");
        view["availableTools"] = tools;
        view["lastOrderResults"] = new JArray(events.Where(item => item.ShipId != null
                && item.Stage is "Tactical" or "Surface defense" or "Air defense" or "Flight Deck" or "Air operations"
                    or "Maritime strike" or "Shore bombardment" or "Approach" or "Conditional attack" or "Contact identification"
                    or "Attack progress" or "Strike assessment")
            .Reverse().Take(12).Select(item => new JObject
            {
                ["groupId"] = item.ShipId, ["shipName"] = item.ShipName, ["targetId"] = item.TargetId,
                ["result"] = item.Status, ["reason"] = item.Detail, ["roeWarning"] = item.RoeWarning
            }));
        DecisionPrompt prompt = DecisionPrompt.ForMission(role, view);
        Briefing = (string?)prompt.Input["briefing"];
        statusMessage = null;
        LastInput = prompt.Input;
        if (role is DecisionRole.Operational or DecisionRole.RoutineTactical) LastAirInput = prompt.Input;
        if (Mode == ControlMode.AiControl && role == DecisionRole.Operational && plan == null) clock.PauseForOpeningPlan();
        CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(60));
        regular = new(role, codex.Decide(role, prompt, cancellation.Token), cancellation, Stopwatch.StartNew(), destinations,
            ships.Cast<ObjectBase>().Concat(role is DecisionRole.Operational or DecisionRole.RoutineTactical && PlayerPicture.Side is { } routeSide
                ? PlayerPicture.Units(routeSide).OfType<Submarine>() : []).ToDictionary(PlayerPicture.Id, RouteSignature), null, toolFollowUp);
        int ownUnits = ships.Length + (view["ownedOtherUnits"]?.Count() ?? 0);
        Add(Stage(role), "Requested", $"{ownUnits} player units · {view["contacts"]?.Count() ?? 0} plotted contacts");

        void Offer(string name, string field, string description) => tools.Add(new JObject
            { ["name"] = name, ["requestField"] = field, ["description"] = description });
    }

    private void Apply(Request work, JObject result, Taskforce side)
    {
        statusMessage = null;
        if (work.Role == DecisionRole.SurfaceDefense)
        {
            ApplyDefense(result, side);
            return;
        }
        if (work.Role == DecisionRole.AirDefense)
        {
            ApplyAirDefense(result, side);
            return;
        }
        bool objectivesChanged = PlayerPicture.Objectives().ToString(Formatting.None) != objectiveState;
        if (objectivesChanged)
        {
            Add(Stage(work.Role), "Discarded · stale", "Objectives changed while Codex was deciding");
            previousSituation = null;
            nextTactical = nextOperational = 0;
            replan = true;
            return;
        }
        Vessel[] ships = PlayerPicture.Ships(side);
        Dictionary<string, Vessel> groups = ships.Where(PlayerPicture.CanNavigate).ToDictionary(PlayerPicture.Id);
        LogCapabilityRequests(work.Role, result["capabilityRequests"]);
        if (!work.ToolFollowUp && LastInput is { } input)
        {
            JArray toolResults = ReadToolResults(result["decisionQueries"], side, input);
            if (work.Role == DecisionRole.Operational && !lookupUsed && (string?)result["inspectGroupId"] is { } requested
                && groups.TryGetValue(requested, out Vessel inspected))
            {
                lookupUsed = true;
                toolResults.Add(new JObject { ["tool"] = "get_owned_unit", ["unit"] = PlayerPicture.Inspect(inspected) });
            }
            if (toolResults.Count > 0)
            {
                JObject refreshed = PlayerPicture.Read(side, ships);
                refreshed["toolResults"] = toolResults;
                Add(Stage(work.Role), "Tool results", $"Read {toolResults.Count} player-view results");
                Start(work.Role, refreshed, ships, NavigationOrders.Destinations(side, ships, chartPoint), toolFollowUp: true);
                return;
            }
        }
        if (work.Role == DecisionRole.Operational)
        {
            string objective = Short((string?)result["objective"], 800);
            if (objective.Length == 0 || result["assignments"] is not JArray assignments) throw new InvalidDataException("Codex returned an incomplete mission plan.");
            ApplyTimePolicy(result["timePolicy"]);
            JArray valid = new();
            HashSet<string> seen = new();
            foreach (JObject assignment in assignments.OfType<JObject>())
            {
                string? id = (string?)assignment["groupId"];
                string? task = (string?)assignment["task"];
                string? destination = (string?)assignment["destinationId"];
                if (id == null || !groups.ContainsKey(id) || !seen.Add(id) || task is not ("Transit" or "Screen" or "Hold")
                    || destination != null && !work.Destinations.Any(point => point.Id == destination && (point.GroupId == null || point.GroupId == id))) continue;
                string reason = Short((string?)assignment["reason"]);
                JObject accepted = new() { ["groupId"] = id, ["task"] = task, ["destinationId"] = destination, ["reason"] = reason };
                valid.Add(accepted);
                Add("Mission Command", task, reason, id);
                if (task == "Screen" && destination?.StartsWith("screen-", StringComparison.Ordinal) == true)
                {
                    Vessel escort = groups[id];
                    Add("Tactical", Waypoint(work, side, escort, id, destination, accepted), reason, id, escort.getName());
                }
            }
            marks = ReadMarks(result["mapMarks"]);
            plan = new JObject { ["objective"] = objective, ["assignments"] = valid, ["mapMarks"] = PlanMarks() };
            reviewingFire = false;
            Add("Mission Command", $"Plan received · {work.Clock.Elapsed.TotalSeconds:0.0}s", objective);
            ApplyAirOrders(result["airOrders"], side);
            ApplyHelicopterOrders(result["helicopterOrders"], side, ships);
            ApplyMaritimeOrders(work, result, side, ships);
            ApplyConditionalAttacks(result["conditionalAttacks"], side);
            ApplyMovementProfiles(result["movementProfiles"], side);
            ApplyConditionalOrders(result["conditionalOrders"], side);
            ApplySensorSweeps(result["sensorSweeps"], side);
            if (Mode == ControlMode.AiControl)
            {
                try
                {
                    if (chart.Replace(side, marks) && marks.Length > 0) Add("Map", "Plan marked", $"{marks.Length} native chart annotations");
                }
                catch (Exception error)
                {
                    chart.Clear();
                    Add("Map", "Failed", error.Message);
                }
            }
            previousSituation = null;
            nextTactical = 0;
            return;
        }

        if (result["orders"] is not JArray orders) throw new InvalidDataException("Codex returned no tactical orders.");
        ApplyTimePolicy(result["timePolicy"]);
        HashSet<string> handled = new();
        foreach (JObject order in orders.OfType<JObject>())
        {
            string? id = (string?)order["groupId"];
            if (id == null || !groups.TryGetValue(id, out Vessel ship) || !handled.Add(id)) continue;
            string shipName = ship.getName();
            string? action = (string?)order["action"];
            string reason = Short((string?)order["reason"]);
            JObject? assignment = plan?["assignments"]?.OfType<JObject>().FirstOrDefault(item => (string?)item["groupId"] == id);
            if (assignment == null) { Add("Tactical", "Rejected · no assignment", reason, id, shipName); continue; }
            string status = action switch
            {
                "Wait" => "Held current orders",
                "RequestReplan" => "Replan requested",
                "SetWaypoint" => Waypoint(work, side, ship, id, (string?)order["destinationId"], assignment),
                _ => "Rejected · unsupported order"
            };
            if (action == "RequestReplan") replan = true;
            Add("Tactical", status, reason, id, shipName);
        }
        ApplyStandingOrders(result["standingOrderChanges"], side);
        ApplyAirOrders(result["airOrders"], side);
        ApplyHelicopterOrders(result["helicopterOrders"], side, ships);
        ApplyMaritimeOrders(work, result, side, ships);
        ApplyConditionalAttacks(result["conditionalAttacks"], side);
        ApplyMovementProfiles(result["movementProfiles"], side);
        ApplyConditionalOrders(result["conditionalOrders"], side);
        ApplySensorSweeps(result["sensorSweeps"], side);
        Add("Tactical", $"Reply received · {work.Clock.Elapsed.TotalSeconds:0.0}s", $"{handled.Count} group decisions");
        if (Mode == ControlMode.AiControl && !replan && (int?)result["timeCompression"] is { } speed
            && LastInput?["availableTimeCompressions"] is JArray offered && offered.Values<int>().Contains(speed)
            && clock.Apply(speed, offered.Values<int>().Max(), Short((string?)result["timeReason"]), side, interrupts.Current))
            Add("Time control", $"Set {GameTime.TimeCompression:0.##}×", Short((string?)result["timeReason"]));
    }

    private void ReadAirPicture(JObject view, Taskforce side)
    {
        view["airDecks"] = PlayerFlightDeck.AirOperations(side);
        view["airTargets"] = PlayerFlightDeck.AirTargets(side);
        view["airWeaponFire"] = PlayerCombat.OwnFireSnapshot(aircraft: true);
        view["airborne"] = PlayerFlightDeck.Airborne(side);
        view["airDefenseAssessment"] = airDefenseAssessment.Read();
        view["airDefenseRouteWarnings"] = AirDefenseRouteWarnings();
        view["heldTransportRouteWarnings"] = airDefenseRoutes.HeldTransportCrossings(side, airDefenseAssessment);
        view["capStations"] = PlayerFlightDeck.CapStations(side);
        view["submarines"] = PlayerOrders.SubmarineStrikeOptions(side);
        view["coastalThreats"] = defense.CoastalThreats(side, (JArray)view["airborne"]!);
        view["strafingOptions"] = PlayerFlightDeck.StrafingOptions(side);
        view["groundAttackOptions"] = PlayerFlightDeck.GroundAttackOptions(side);
    }

    private void UseTorpedoWindows(Taskforce side)
    {
        double now = GameTime.missionElapsedTime;
        if (now < nextTorpedoCheck) return;
        nextTorpedoCheck = now + 3;
        foreach (Submarine sub in PlayerPicture.Units(side).OfType<Submarine>())
        {
            if (sub._weaponStatus == ObjectBase.WeaponStatus.Hold || PlayerOrders.HasTorpedoOrder(sub)) continue;
            string id = PlayerPicture.Id(sub);
            var targets = PlayerOrders.SubmarineTorpedoTargets(side, sub).Where(track => track.Identified.Value)
                .OrderByDescending(track => PlayerOrders.SubmarineUnderImmediateThreat(sub, track))
                .ThenBy(track => GeoPosition.HaversineDistanceNmi(sub.getGeoPosition(), track.PositionEstimate!.Value.Item1));
            foreach (Vehicle track in targets)
            {
                string targetId = PlayerPicture.Id(track);
                if (nextTorpedoAttempt.GetValueOrDefault((id, targetId)) > now || PlayerCombat.TorpedoInbound(track.BaseObject)) continue;
                nextTorpedoAttempt[(id, targetId)] = now + 30;
                SurfaceOrderResult outcome = EngageMaritimeTarget(sub, side, track, SurfaceWeapon.BestAvailable, preferTorpedoes: false);
                TrackFire(id, targetId, null, outcome.Status, outcome.Detail, AttackAccepted(outcome.Status));
                Add("Maritime strike", outcome.Status, $"{PlayerPicture.ContactLabel(track)} · Live torpedo opportunity · {outcome.Detail}",
                    id, sub.DisplayNameShort, targetId: targetId);
                if (!outcome.Status.Contains("queued", StringComparison.Ordinal)) continue;
                nextTactical = 0;
                break;
            }
        }
    }

    private void UseGroundOpportunities(Taskforce side)
    {
        double now = GameTime.missionElapsedTime;
        if (now < nextGroundOpportunity) return;
        nextGroundOpportunity = now + 5;
        HashSet<ObjectBase> tasked = [];
        HashSet<string> targeted = [];
        foreach (var option in PlayerFlightDeck.GroundOpportunities(side))
        {
            string targetId = PlayerPicture.Id(option.Track);
            if (tasked.Contains(option.Unit) || targeted.Contains(targetId) || option.Track.PositionEstimate?.Item1 is not { } fix
                || !airDefenseRoutes.GroundAttackPathClear(side, airDefenseAssessment, option.Unit.getGeoPosition(), fix, option.ReleaseNm)) continue;
            string id = PlayerPicture.Id(option.Unit);
            string status;
            try { status = PlayerFlightDeck.TaskAirborne(side, id, option.Role, [], useRadar: false, targetId); }
            catch (Exception error)
            {
                Add("Air operations", "Opportunity failed", Short(error.Message), id, option.Unit.DisplayNameShort, targetId: targetId);
                continue;
            }
            if (!IsAppliedUnitOrder("Air operations", status)) continue;
            TrackFire(id, targetId, AirOrderWeapon(status), status, "", accepted: true);
            tasked.Add(option.Unit);
            targeted.Add(targetId);
            Add("Air operations", status, $"Nearby exposed {PlayerPicture.ContactLabel(option.Track)} · live opportunity",
                id, option.Unit.DisplayNameShort, targetId: targetId);
            RecordFlightAssignment(id, option.Role, targetId, status);
            nextTactical = 0;
        }
    }

    private void ReportDepartureOrder(PlayerFlightDeck.AirDepartureResult result)
    {
        string detail = result.TargetId == null ? $"{result.Role} · Patrol assigned at launch" : $"{result.Role} · Attack assigned at launch";
        Add("Air operations", result.Status, detail, result.UnitId, result.Name, targetId: result.TargetId);
        if (result.TargetId is { } targetId)
            TrackFire(result.UnitId, targetId, AirOrderWeapon(result.Status), result.Status, "", AttackAccepted(result.Status));
        RecordFlightAssignment(result.UnitId, result.Role, result.TargetId, result.Status);
        if (!result.Status.StartsWith("Rejected", StringComparison.Ordinal)) return;
        nextTactical = 0;
        previousSituation = null;
    }

    private void ApplyAirOrders(JToken? proposed, Taskforce side)
    {
        if (proposed is not JArray orders || LastInput is not { } input) return;
        Dictionary<string, ObjectBase> units = PlayerPicture.Units(side).ToDictionary(PlayerPicture.Id);
        HashSet<string> tasked = [];
        HashSet<(string Unit, string Action)> reorganized = [];
        HashSet<(string Unit, string Fit)> launched = [];
        foreach (JObject order in orders.OfType<JObject>().Take(8)
            .OrderByDescending(order => (string?)order["action"] is ("Launch" or "Prepare") && (string?)order["role"] == "CAP"))
        {
            string? id = (string?)order["unitId"];
            if (id == null || !units.TryGetValue(id, out ObjectBase unit) || (string?)order["role"] is not { } role) continue;
            string? targetId = (string?)order["targetId"];
            string? action = (string?)order["action"];
            string reason = Short((string?)order["reason"]);
            PendingLaunchTask? deckTask = null;
            OrderStatus? deckStatus = null;
            string outcome;
            try
            {
                outcome = ApplyAirOrder(order, unit, id, role, targetId, task => deckTask = task,
                    () => { if (deckStatus != null) deckStatus.Launches++; });
            }
            catch (Exception error) { outcome = $"Failed · {error.Message}"; }
            if (Mode == ControlMode.AiControl)
            {
                if (action is "Launch" or "Prepare")
                {
                    if (deckTask == null || !orderStatuses.Values.Any(status => status.Active && status.DeckTask == deckTask && status.Action == action))
                    {
                        deckStatus = NewOrder(id, action);
                        deckStatus.DeckTask = deckTask;
                        deckStatus.LaunchCount = action == "Launch" ? (int?)order["count"] ?? 1 : 0;
                        deckStatus.Set(OrderRejected(outcome) ? "blocked" : deckTask != null ? "executing" : "completed", outcome);
                    }
                }
                else if (action == "Task" && targetId != null) TrackFire(id, targetId, AirOrderWeapon(outcome), outcome, "", AttackAccepted(outcome));
                else if (action == "Task" && role is "Transport" or "Hold" or "Disengage" or "Recon" or "ASW")
                    TrackRoute(unit, role, outcome);
            }
            Add("Air operations", outcome, $"{role} · {reason}", id, unit.DisplayNameShort, targetId: targetId);
            if ((string?)order["action"] == "Task") RecordFlightAssignment(id, role, targetId, outcome);
            if (outcome.StartsWith("Joined", StringComparison.Ordinal)) flightAssignments.Remove(id);
        }

        string ApplyAirOrder(JObject order, ObjectBase unit, string id, string role, string? targetId,
            Action<PendingLaunchTask> trackTask, Action onLaunched)
        {
            if (!TryReadAirRoute(order["route"], out GeoPosition[] route)) return "Rejected · invalid air route";
            string? action = (string?)order["action"];
            if (action is "Split" or "Join")
            {
                if (role != "Formation" || route.Length != 0 || !WasAirborne(id) || !reorganized.Add((id, action)))
                    return "Rejected · invalid or repeated formation order";
                if (action == "Split" && targetId != null) return "Rejected · Split takes no destination leader";
                ObjectBase? leader = null;
                if (action == "Join" && (targetId == null || !WasAirborne(targetId) || !units.TryGetValue(targetId, out leader)))
                    return "Rejected · Join requires an owned airborne leader";
                return Mode == ControlMode.AiControl ? PlayerFlightDeck.ReorganizeFlight(side, unit, action, leader) : $"{action} suggested";
            }
            if (targetId != null && !WasLiveContact(input, targetId))
                return "Rejected · target was not a live plotted contact";
            bool useRadar = (bool?)order["useRadar"] == true;
            if (action is ("Launch" or "Prepare") && (string?)order["fitId"] is { } fitId
                && (int?)order["count"] is int count && count is >= 1 and <= 4)
            {
                JToken? deck = input["airDecks"]?.FirstOrDefault(item => (string?)item["shipId"] == id);
                JToken? fit = deck?["fits"]?.FirstOrDefault(candidate => (string?)candidate["id"] == fitId);
                if (fit?["roles"]?.Values<string>().Contains(role) != true)
                    return "Rejected · fit was not offered for this role";
                string? refitBatchId = (string?)order["refitBatchId"];
                if (refitBatchId != null && fit?["refitBatches"]?.Any(batch => (string?)batch["id"] == refitBatchId
                    && (int?)batch["count"] == count) != true) return "Rejected · refit batch was not offered for this fit and count";
                if (!launched.Add((id, fitId))) return "Kept · fit already ordered this decision";
                bool prepare = action == "Prepare";
                if (!prepare && fit?["helicopter"]?.Value<bool>() == true
                    && defense.HelicopterRouteBlocker(side, unit.getGeoPosition(), route, repeat: role is "Recon" or "ASW" or "AntiShip" or "Strike") is { } launchBlocker)
                    return $"Rejected · helicopter route enters {launchBlocker}";
                return Mode == ControlMode.AiControl ? PlayerFlightDeck.QueueAirMission(unit, side, fitId, role, count, route, useRadar, targetId, prepare,
                        trackTask: trackTask, launched: onLaunched, refitBatchId: refitBatchId)
                    : $"{(prepare ? "Preparation" : "Launch")} suggested · {count}× {fitId}";
            }
            if (action != "Task" || !WasAirborne(id))
                return "Rejected · invalid air order or aircraft was not airborne";
            if (role == "Strafe" && input["strafingOptions"]?.Any(option => (string?)option["unitId"] == id
                && (string?)option["targetId"] == targetId) != true) return "Rejected · no compatible strafing option was offered";
            if (!tasked.Add(id)) return "Kept · flight already tasked this decision";
            if (role == "Transport" && targetId == null && unit is Helicopter
                && (route.Length == 0 ? airDefenseRoutes.TransportRouteBlocked(side, airDefenseAssessment, id)
                    : airDefenseRoutes.Intersects(side, airDefenseAssessment, unit.getGeoPosition(), route, repeat: false)))
                return "Rejected · transport route still crosses a known air-defense envelope";
            if (role == "Hold" && targetId == null && route.Length == 0)
            {
                GeoPosition origin = unit.getGeoPosition();
                foreach (float offset in new[] { 180f, 90f, -90f, 0f })
                {
                    GeoPosition turn = origin.GeoOffsetByDistanceOnHeading(4, unit.Heading.Value + offset);
                    GeoPosition[] hold = [turn, origin];
                    if (airDefenseRoutes.Intersects(side, airDefenseAssessment, origin, hold)) continue;
                    route = hold;
                    break;
                }
                if (route.Length == 0) return "Rejected · no clear local hold, supply a route out of coverage";
            }
            if (unit is Helicopter && defense.HelicopterRouteBlocker(side, unit.getGeoPosition(), route,
                repeat: role is "Recon" or "ASW" or "Hold" or "AntiShip" or "Strike") is { } blocker)
                return $"Rejected · helicopter route enters {blocker}";
            return Mode == ControlMode.AiControl ? PlayerFlightDeck.TaskAirborne(side, id, role, route, useRadar, targetId,
                targetId != null && airDefenseAssessment.LikelySuppressed(targetId)) : "Air task suggested";
        }

        bool WasAirborne(string id) => input["airborne"]?.Any(item => (string?)item["id"] == id) == true;
    }

    private void RecordFlightAssignment(string id, string role, string? targetId, string outcome)
    {
        if (!IsAppliedUnitOrder("Air operations", outcome)) return;
        if (targetId != null || role is ("Strike" or "SEAD" or "AntiShip"))
            flightAssignments[id] = new(role, targetId, GameTime.missionElapsedTime);
        else flightAssignments.Remove(id);
    }

    private static bool WasLiveContact(JObject input, string targetId) =>
        input["contacts"]?.Any(track => (string?)track["id"] == targetId && (bool?)track["live"] == true) == true
        || input["airTargets"]?.Any(track => (string?)track["id"] == targetId) == true;

    private void ApplyMaritimeOrders(Request work, JObject result, Taskforce side, Vessel[] ships)
    {
        if (LastInput is not { } input) return;
        Dictionary<string, ObjectBase> owned = ships.Cast<ObjectBase>().Concat(PlayerPicture.Units(side).OfType<Submarine>())
            .ToDictionary(PlayerPicture.Id);
        HashSet<string> handled = [];
        foreach (JObject order in result["maritimeOrders"]?.OfType<JObject>().Take(8) ?? [])
        {
            if ((string?)order["shipId"] is not { } id || !owned.TryGetValue(id, out ObjectBase ship)
                || (string?)order["targetId"] is not { } targetId || !WasLiveContact(input, targetId) || !handled.Add(id)) continue;
            string? action = (string?)order["action"];
            if (action is "EngageShip" or "Bombard")
                ApplySurfaceOrder(order, side, ship, id, allowDeferred: false, maritimeAction: action);
        }

        handled.Clear();
        foreach (JObject order in result["approaches"]?.OfType<JObject>().Take(4) ?? [])
        {
            if ((string?)order["shipId"] is not { } id || !owned.TryGetValue(id, out ObjectBase unit)
                || (string?)order["targetId"] is not { } targetId || !handled.Add(id)) continue;
            bool wasStagingContact = unit is Submarine && input["submarines"]?.Any(sub => (string?)sub["shipId"] == id
                && sub["stagingContacts"]?.Any(contact => (string?)contact["targetId"] == targetId) == true) == true;
            if (unit is Submarine ? !wasStagingContact : !WasLiveContact(input, targetId)) continue;
            string outcome;
            try
            {
                if (!work.Routes.TryGetValue(id, out string route) || route != RouteSignature(unit)) outcome = "Rejected · unit route changed";
                else if (PlayerPicture.LiveContact(side, targetId) is not { } track) outcome = "Rejected · no live position";
                else if (unit is Submarine sub) outcome = NavigationOrders.StageSubmarine(sub, side, track, Mode == ControlMode.Observe);
                else if (defense.ActiveGunfire) outcome = "Rejected · friendly units are under observed gunfire";
                else if (unit is Vessel ship && track.CurrentRelationship() == RelationsState.Hostile)
                    outcome = NavigationOrders.Approach(ship, side, track, defense, (double?)order["standoffNm"], Mode == ControlMode.Observe);
                else outcome = "Rejected · no live hostile position";
            }
            catch (Exception error) { outcome = $"Rejected · {Short(error.InnerException?.Message ?? error.Message)}"; }
            if (!outcome.StartsWith("Kept · already near", StringComparison.Ordinal)) TrackRoute(unit, "Approach", outcome);
            Add("Approach", outcome, Short((string?)order["reason"]), id, unit.DisplayNameShort, targetId: targetId);
        }

        foreach (JObject order in result["surfaceRoutes"]?.OfType<JObject>().Take(4) ?? [])
        {
            if ((string?)order["shipId"] is not { } id || !owned.TryGetValue(id, out ObjectBase unit) || unit is not Vessel ship
                || !handled.Add(id)) continue;
            string outcome;
            try
            {
                if (!work.Routes.TryGetValue(id, out string route) || route != RouteSignature(ship)) outcome = "Rejected · ship route changed";
                else if (!TryReadSurfaceRoute(order["route"], out GeoPosition[] points)) outcome = "Rejected · invalid surface route";
                else outcome = NavigationOrders.SetSurfaceRoute(ship, side, points, defense, Mode == ControlMode.Observe);
            }
            catch (Exception error) { outcome = $"Rejected · {Short(error.InnerException?.Message ?? error.Message)}"; }
            TrackRoute(unit, "Surface route", outcome);
            Add("Approach", outcome, Short((string?)order["reason"]), id, ship.getName());
        }
    }

    private static bool TryReadSurfaceRoute(JToken? value, out GeoPosition[] route)
    {
        route = [];
        if (value is not JArray { Count: >= 1 and <= 8 } points) return false;
        List<GeoPosition> valid = new(points.Count);
        foreach (JToken point in points)
        {
            if (point is not JObject || (double?)point["latitude"] is not { } latitude || (double?)point["longitude"] is not { } longitude
                || !double.IsFinite(latitude) || !double.IsFinite(longitude) || Math.Abs(latitude) >= 90 || Math.Abs(longitude) > 180)
                return false;
            valid.Add(new() { _latitude = latitude, _longitude = longitude });
        }
        route = valid.ToArray();
        return true;
    }

    private static bool TryReadAirRoute(JToken? value, out GeoPosition[] route)
    {
        route = [];
        if (value is not JArray points || points.Count > 8) return false;
        List<GeoPosition> valid = new(points.Count);
        foreach (JToken point in points)
        {
            if (point is not JObject || (double?)point["latitude"] is not { } lat || (double?)point["longitude"] is not { } lon
                || (double?)point["altitudeFeet"] is not { } feet || !double.IsFinite(lat) || Math.Abs(lat) > 90
                || !double.IsFinite(lon) || Math.Abs(lon) > 180 || !double.IsFinite(feet) || feet is < 100 or > 60000) return false;
            valid.Add(new GeoPosition { _latitude = lat, _longitude = lon, _height = feet / 220.47266 });
        }
        route = valid.ToArray();
        return true;
    }

    private void ApplyHelicopterOrders(JToken? proposed, Taskforce side, Vessel[] ships)
    {
        if (proposed is not JArray orders || LastInput?["helicopterDecks"] is not JArray decks) return;
        HashSet<string> assigned = [];
        foreach (JObject order in orders.OfType<JObject>().Take(4))
        {
            string? shipId = (string?)order["shipId"];
            string? fitId = (string?)order["fitId"];
            if (shipId == null || fitId == null || !decks.Any(deck => (string?)deck["shipId"] == shipId
                    && deck["screenFits"] is JArray fits && fits.Any(fit => (string?)fit["id"] == fitId)) || !assigned.Add(shipId)
                || ships.FirstOrDefault(ship => PlayerPicture.Id(ship) == shipId) is not { } host) continue;
            string? anchorId = (string?)order["anchorId"];
            GeoPosition origin = host.getGeoPosition();
            float heading = host.Heading.Value;
            GeoPosition center = origin.GeoOffsetByDistanceOnHeading(8, heading);
            string area = "Ahead of the formation";
            if (anchorId != null)
            {
                Vehicle? anchor = SurfaceDefense.ProtectedContacts(side).FirstOrDefault(track => PlayerPicture.Id(track) == anchorId);
                if (anchor?.PositionEstimate?.Item1 is not { } position || GeoPosition.HaversineDistanceNmi(origin, position) > 25)
                {
                    Add("Flight Deck", "Rejected · escort anchor unavailable or beyond 25 nm", anchorId, shipId, host.getName());
                    continue;
                }
                center = position;
                area = $"Screen {PlayerPicture.ContactLabel(anchor)}";
            }
            center._height = 800 / 220.47266;
            GeoPosition[] route = [center.GeoOffsetByDistanceOnHeading(3, heading - 90), center.GeoOffsetByDistanceOnHeading(4, heading),
                center.GeoOffsetByDistanceOnHeading(3, heading + 90), center];
            if (defense.HelicopterRouteBlocker(side, origin, route, repeat: true) is { } blocker)
            {
                Add("Flight Deck", "Rejected · helicopter screen crosses ship air defenses", blocker, shipId, host.getName());
                continue;
            }
            string outcome = Mode == ControlMode.AiControl
                ? PlayerFlightDeck.QueueHelicopterScreen(host, side, fitId, route, (bool?)order["useRadar"] == true) : $"Helicopter screen suggested · {fitId}";
            Add("Flight Deck", outcome, $"{area} · {Short((string?)order["reason"])}", shipId, host.getName());
        }
    }

    private void ApplyTimePolicy(JToken? proposed)
    {
        if (proposed is not JObject policy) return;
        string suggestion = Short((string?)policy["defaultSuggestion"], 900).Trim();
        if (suggestion.Length > 0 && loggedTimeSuggestions.Add(suggestion))
        {
            lastTimeSuggestion = suggestion;
            UnityEngine.Debug.Log($"[OODA] Time defaults suggestion | {mission} | {suggestion}");
        }
        if (!ManagingClock
            || LastInput?["timeInterrupts"]?["allowedSpeeds"] is not JArray offered || offered.Count == 0) return;
        HashSet<int> speeds = offered.Values<int>().ToHashSet();
        string reason = Short((string?)policy["cruiseReason"]).Trim();
        if ((int?)policy["cruiseSpeed"] is { } speed && speeds.Contains(speed) && reason.Length > 0 && clock.SetCruise(speed, reason))
            Add("Time control", $"Cruise set to {speed}×", reason);
        if (policy["rules"] is JArray entries) ApplyTimeRules(entries, speeds);
    }

    private void ApplyTimeRules(JArray entries, HashSet<int> speeds)
    {
        HashSet<string> subjects = new(StringComparer.Ordinal);
        foreach (string field in new[] { "contacts", "ownedUnits", "ownedOtherUnits" })
            if (LastInput?[field] is JArray units)
                foreach (JObject unit in units.OfType<JObject>())
                    if ((string?)unit["id"] is { } id) subjects.Add(id);
        if (LastInput?["timeInterrupts"]?["rules"] is JArray previous)
            foreach (JObject rule in previous.OfType<JObject>())
                if ((string?)rule["subjectId"] is { } id) subjects.Add(id);
        if (LastInput?["timeInterrupts"]?["recentEvents"] is JArray observed)
            foreach (JObject item in observed.OfType<JObject>())
            {
                if ((string?)item["subjectId"] is { } id) subjects.Add(id);
                if ((string?)item["missileId"] is { } missileId) subjects.Add(missileId);
            }

        List<TimeRule> next = [];
        foreach (JObject item in entries.OfType<JObject>().Take(16))
        {
            string? subject = (string?)item["subjectId"];
            if (subject != null && !subjects.Contains(subject)) continue;
            if ((string?)item["event"] is not { } name || !Enum.TryParse(name, out TimeEventKind kind) || kind.ToString() != name
                || (int?)item["speed"] is not { } speed || !speeds.Contains(speed)
                || (int?)item["holdSeconds"] is not { } hold || hold is < 2 or > 60) continue;
            next.Add(new(kind, subject, (string?)item["category"], (string?)item["posture"], speed, hold));
        }
        TimeRule[] rules = next.Distinct().ToArray();
        if ((entries.Count == 0 || rules.Length > 0) && interrupts.Replace(rules, UnityEngine.Time.realtimeSinceStartupAsDouble))
            Add("Time control", "Interrupt rules updated", $"{rules.Length} custom rules");
    }

    private void ApplyDefense(JObject result, Taskforce side)
    {
        if (result["engagements"] is not JArray orders) throw new InvalidDataException("Codex returned no surface engagements list.");
        if (orders.Count == 0)
        {
            Add("Surface defense", "Held fire", Short((string?)result["reason"]));
            return;
        }
        Dictionary<string, Vessel> ships = PlayerPicture.Ships(side).ToDictionary(PlayerPicture.Id);
        HashSet<string> assigned = [];
        foreach (JObject order in orders.OfType<JObject>())
        {
            if ((string?)order["shipId"] is { } shipId && ships.TryGetValue(shipId, out Vessel ship) && assigned.Add(shipId))
            {
                deferredFire.Remove(shipId);
                ApplySurfaceOrder(order, side, ship, shipId);
            }
            else Add("Surface defense", "Rejected · invalid or duplicate responder", Short((string?)order["reason"]));
        }
    }

    private void RespondToObservedGunfire(Taskforce side)
    {
        if (defense.NewFriendlyAttacks.Length == 0) return;
        Vessel[] ships = PlayerPicture.Ships(side);
        foreach (PlayerCombat.Gunfire attack in defense.NewFriendlyAttacks.GroupBy(item => item.AttackerId).Select(group => group.First()))
        {
            if (PlayerPicture.LiveContact(side, attack.AttackerId) is not { BaseObject: Vessel } track
                || track.CurrentRelationship() != RelationsState.Hostile || track.PositionEstimate?.Item1 is not { } position) continue;
            if (ships.Any(ship => ship._currentEngageTasks.Any(task => task._targetObject == track.BaseObject))) continue;
            Vessel[] responders = ships.Where(ship => ship._weaponStatus != ObjectBase.WeaponStatus.Hold)
                .OrderBy(ship => ship._currentEngageTasks.Any(task => task._targetObject is Vessel))
                .ThenBy(ship => GeoPosition.HaversineDistanceNmi(ship.getGeoPosition(), position)).ToArray();
            string detail = $"{PlayerPicture.ContactLabel(track)} was observed firing at {attack.Victim}";
            if (!TryFire(SurfaceWeapon.Guns)) TryFire(SurfaceWeapon.Missiles);

            bool TryFire(SurfaceWeapon weapon)
            {
                foreach (Vessel ship in responders)
                {
                    SurfaceOrderResult outcome;
                    try { outcome = PlayerOrders.EngageSurface(ship, side, track, weapon); }
                    catch (Exception error) { outcome = new("Rejected", Short(error.Message)); }
                    Add("Surface defense", outcome.Status, $"{detail} · {outcome.Detail}", PlayerPicture.Id(ship), ship.getName(), targetId: attack.AttackerId);
                    if (outcome.Status is "Gun order queued" or "Already engaging" or "Missile order queued"
                        or "Missile and gun orders queued" or "Missile held") return true;
                }
                return false;
            }
        }
    }

    private void ApplySurfaceOrder(JObject order, Taskforce side, ObjectBase ship, string shipId, bool allowDeferred = true, string? maritimeAction = null)
    {
        bool bombard = maritimeAction == "Bombard";
        string stage = bombard ? "Shore bombardment" : maritimeAction == "EngageShip" ? "Maritime strike" : "Surface defense";
        string reason = Short((string?)order["reason"]);
        string? targetId = (string?)order["targetId"];
        Vehicle? target = targetId == null ? null : PlayerPicture.LiveContact(side, targetId);
        if (target == null || (bombard ? target.BaseObject is not LandUnit : target.BaseObject is not Vessel))
        {
            double observedAt = (double?)LastInput?["missionClock"]?["elapsedSeconds"] ?? GameTime.missionElapsedTime;
            bool defer = allowDeferred && Mode == ControlMode.AiControl && targetId != null && LastInput?["contacts"] is JArray contacts
                && contacts.Any(track => (string?)track["id"] == targetId && (bool?)track["live"] == true);
            if (defer)
                deferredFire[shipId] = new(order, targetId!, UnityEngine.Time.realtimeSinceStartupAsDouble + 20,
                    GameTime.missionElapsedTime + 90);
            Add(stage, defer ? "Deferred · lost target track" : $"Rejected · no live {(bombard ? "shore" : "surface")} target",
                $"{targetId ?? "No target"} · {GameTime.missionElapsedTime - observedAt:0}s since the decision snapshot · {reason}",
                shipId, ship.getName(), targetId: targetId);
            return;
        }
        if ((string?)order["weapons"] is not { } name || !Enum.TryParse(name, out SurfaceWeapon weapon) || weapon.ToString() != name)
        {
            Add(stage, "Rejected · invalid weapon choice", reason, shipId, ship.getName(), targetId: targetId);
            return;
        }
        List<string> warnings = [];
        if (maritimeAction == null && defense.ReviewEvidence(order, side, target) is { } evidenceWarning) warnings.Add(evidenceWarning);
        RelationsState relationship = target.CurrentRelationship();
        if (relationship is RelationsState.Friendly or RelationsState.Neutral || maritimeAction != null && relationship != RelationsState.Hostile)
            warnings.Add($"The target is plotted as {relationship}.");
        if (ship._weaponStatus == ObjectBase.WeaponStatus.Hold) warnings.Add("The responding ship is on Weapons Hold.");
        if ((string?)order["roeAssessment"] != "Consistent")
        {
            string assessment = Short((string?)order["roeReason"]);
            warnings.Add(assessment.Length > 0 ? assessment : "The decision did not establish that the fire was authorized.");
        }
        string? warning = warnings.Count > 0 ? string.Join(" ", warnings) : null;
        string targetName = PlayerPicture.ContactLabel(target);
        SurfaceOrderResult outcome;
        try
        {
            outcome = Mode != ControlMode.AiControl ? new($"{weapon} suggested")
                : bombard ? ship is Vessel vessel ? PlayerOrders.EngageLand(vessel, side, target, weapon)
                    : new("Rejected", "Assign shore bombardment to a surface ship")
                : EngageMaritimeTarget(ship, side, target, weapon, preferTorpedoes: maritimeAction == "EngageShip" && relationship == RelationsState.Hostile);
        }
        catch (Exception error) { outcome = new("Rejected", Short(error.Message)); }
        string detail = outcome.Detail.Length > 0 ? $"{targetName} · {outcome.Detail} · {reason}" : $"{targetName} · {reason}";
        Add(stage, outcome.Status, detail, shipId, ship.getName(), warning, targetId);
        if (targetId != null) TrackFire(shipId, targetId, null, outcome.Status, outcome.Detail, AttackAccepted(outcome.Status));
        if (maritimeAction == null && outcome.Status == "Guns unavailable" && targetId != null) defense.ReviewGunFailure(targetId);
        if (warning != null)
        {
            reviewingFire = true;
            replan = true;
            nextOperational = 0;
        }
    }

    private SurfaceOrderResult EngageMaritimeTarget(ObjectBase shooter, Taskforce side, Vehicle target, SurfaceWeapon weapon, bool preferTorpedoes)
    {
        if (shooter is Submarine submarine)
        {
            if (weapon == SurfaceWeapon.Guns) return new("Rejected", "Use Torpedoes or BestAvailable for a submarine");
            SurfaceOrderResult torpedo = PlayerOrders.EngageWithTorpedoes(submarine, side, target);
            if (weapon is not (SurfaceWeapon.Missiles or SurfaceWeapon.BestAvailable)
                || !PlayerOrders.SubmarineUnderImmediateThreat(submarine, target)) return torpedo;
            SurfaceOrderResult missile = PlayerOrders.EngageSubmarineMissile(submarine, side, target);
            return new(missile.Status == "Missile order queued" && torpedo.Status == "Torpedo order queued"
                ? "Missile and torpedo orders queued" : missile.Status == "Missile order queued" ? missile.Status : torpedo.Status,
                $"{torpedo.Status}: {torpedo.Detail} · {missile.Status}: {missile.Detail}");
        }

        if (preferTorpedoes && weapon is (SurfaceWeapon.Missiles or SurfaceWeapon.BestAvailable))
        {
            bool inbound = PlayerCombat.TorpedoInbound(target.BaseObject);
            bool immediate = false;
            foreach (Submarine candidate in PlayerPicture.Units(side).OfType<Submarine>()
                .Where(unit => unit._weaponStatus != ObjectBase.WeaponStatus.Hold && PlayerOrders.InTorpedoEnvelope(unit, target)))
            {
                SurfaceOrderResult torpedo = PlayerOrders.EngageWithTorpedoes(candidate, side, target);
                if (torpedo.Status is not ("Torpedo order queued" or "Already engaging" or "Torpedo held")) continue;
                if (torpedo.Status == "Torpedo order queued") Add("Maritime strike", torpedo.Status,
                    $"{PlayerPicture.ContactLabel(target)} · {torpedo.Detail} · In-range torpedo preferred over a ship-launched missile",
                    PlayerPicture.Id(candidate), candidate.DisplayNameShort, targetId: PlayerPicture.Id(target));
                if (PlayerOrders.SubmarineUnderImmediateThreat(candidate, target)) { immediate = true; break; }
                return new("Missile held", $"{candidate.DisplayNameShort} has a torpedo order for this target");
            }
            if (inbound && !immediate) return new("Missile held", "A friendly torpedo is already inbound");
        }
        return PlayerOrders.EngageSurface((Vessel)shooter, side, target, weapon);
    }

    private void RetrySurfaceOrders(Taskforce side)
    {
        if (deferredFire.Count == 0) return;
        foreach (var (shipId, pending) in deferredFire.ToArray())
        {
            if (UnityEngine.Time.realtimeSinceStartupAsDouble >= pending.WallDeadline || GameTime.missionElapsedTime >= pending.MissionDeadline)
            {
                deferredFire.Remove(shipId);
                Add("Surface defense", "Deferred fire expired", "No live track returned before the order expired", shipId, targetId: pending.TargetId);
                continue;
            }
            if (PlayerPicture.LiveContact(side, pending.TargetId) is not { BaseObject: Vessel } target) continue;
            deferredFire.Remove(shipId);
            Vessel? ship = PlayerPicture.Ships(side).FirstOrDefault(unit => PlayerPicture.Id(unit) == shipId);
            if (ship == null) continue;
            RelationsState posture = target.CurrentRelationship();
            if (posture is RelationsState.Friendly or RelationsState.Neutral
                || posture != RelationsState.Hostile && defense.ReviewEvidence(pending.Order, side, target) != null)
            {
                Add("Surface defense", "Deferred fire cleared", "Reacquired contact no longer supports the earlier order", shipId, ship.getName(), targetId: pending.TargetId);
                defense.Pending = true;
                continue;
            }
            Add("Surface defense", "Target reacquired", "Rechecking the deferred fire order", shipId, ship.getName(), targetId: pending.TargetId);
            ApplySurfaceOrder(pending.Order, side, ship, shipId, allowDeferred: false);
        }
    }

    private void ApplyAirDefense(JObject result, Taskforce side)
    {
        ApplyStandingOrders(result["standingOrderChanges"], side);
        string reason = Short((string?)result["reason"]);
        if (result["targetIds"] is not JArray { Count: > 0 } ids || LastInput?["contacts"] is not JArray contacts)
        {
            Add("Air defense", "Raid response", reason);
            return;
        }
        Vessel[] ships = PlayerPicture.Ships(side);
        HashSet<Vessel> assigned = [];
        foreach (string id in ids.Values<string>().OfType<string>().Distinct().Take(4))
        {
            if (!contacts.Any(item => (string?)item["id"] == id && (string?)item["posture"] == "Hostile")
                || PlayerPicture.LiveContact(side, id) is not { } track || !track.BaseObject.IsAirUnit
                || track.CurrentRelationship() != RelationsState.Hostile || track.PositionEstimate is not { } estimate) continue;
            string target = PlayerPicture.ContactLabel(track);
            if (ships.Any(ship => ship._currentEngageTasks.Any(task => task._targetObject == track.BaseObject)))
            {
                Add("Air defense", "Already engaging", target);
                continue;
            }
            Vessel? responder = null;
            foreach (Vessel ship in ships.OrderBy(assigned.Contains)
                .ThenBy(ship => GeoPosition.HaversineDistanceNmi(ship.getGeoPosition(), estimate.Item1)))
            {
                if (Mode != ControlMode.Observe && !PlayerOrders.EngageAir(ship, side, track)) continue;
                responder = ship;
                break;
            }
            if (responder == null) Add("Air defense", "No air-defense weapon available", target);
            else
            {
                assigned.Add(responder);
                TrackFire(PlayerPicture.Id(responder), id, null, "Intercept queued", "", accepted: true);
                Add("Air defense", Mode == ControlMode.Observe ? "Intercept suggested" : "Intercept queued",
                    $"{target} · {reason}", PlayerPicture.Id(responder), responder.getName());
            }
        }
    }

    private static ChartMark[] ReadMarks(JToken? proposed)
    {
        List<ChartMark> result = [];
        foreach (JObject mark in proposed?.OfType<JObject>().Take(8) ?? Enumerable.Empty<JObject>())
        {
            if (!Enum.TryParse((string?)mark["kind"], out ChartMarkKind kind) || !Enum.IsDefined(typeof(ChartMarkKind), kind)
                || (double?)mark["latitude"] is not { } latitude || (double?)mark["longitude"] is not { } longitude
                || !double.IsFinite(latitude) || !double.IsFinite(longitude) || Math.Abs(latitude) > 90 || Math.Abs(longitude) > 180) continue;
            string label = Short((string?)mark["label"], 80).Trim();
            float? radius = kind == ChartMarkKind.SearchArea ? (float?)mark["radiusNm"] : null;
            if (label.Length == 0 || kind == ChartMarkKind.SearchArea && radius is not (> 0 and <= 150)) continue;
            result.Add(new(kind, label, new GeoPosition { _latitude = latitude, _longitude = longitude }, radius));
        }
        return result.ToArray();
    }

    private void ApplyStandingOrders(JToken? changes, Taskforce side)
    {
        if (changes is not JArray proposed || LastInput?["standingOrders"] is not JArray targets) return;
        HashSet<(string, string)> handled = [];
        foreach (JObject change in proposed.OfType<JObject>().Take(4))
        {
            if ((string?)change["targetId"] is not { } targetId || (string?)change["settingId"] is not { } settingId
                || (string?)change["value"] is not { } value || !handled.Add((targetId, settingId))) continue;
            JObject? target = targets.OfType<JObject>().FirstOrDefault(item => (string?)item["targetId"] == targetId);
            JObject? setting = target?["settings"]?.OfType<JObject>().FirstOrDefault(item => (string?)item["id"] == settingId);
            if (setting == null) continue;
            string result = Mode == ControlMode.Observe ? $"Suggested · {setting["label"]} = {value}"
                : PlayerDoctrine.Set(side, targetId, settingId, value, (string?)setting["value"], (bool?)setting["overridden"]);
            Add("Standing Orders", result, $"{target!["name"]} · {Short((string?)change["reason"])}", targetId);
        }
    }

    private string Waypoint(Request work, Taskforce side, Vessel ship, string id, string? destinationId, JObject assignment)
    {
        if ((string?)assignment["task"] == "Hold") return "Rejected · assignment is to hold";
        if (destinationId == null || (string?)assignment["destinationId"] != destinationId
            || work.Destinations.FirstOrDefault(item => item.Id == destinationId && (item.GroupId == null || item.GroupId == id)) is not { } destination)
            return "Rejected · destination is outside the assignment";
        if (!work.Routes.TryGetValue(id, out string route) || route != RouteSignature(ship)) return "Rejected · existing orders changed while Codex was deciding";
        if (Mode == ControlMode.Observe) return $"Suggested waypoint · {destination.Label}";
        try
        {
            string outcome = NavigationOrders.SetWaypoint(ship, side, destination);
            TrackRoute(ship, "Waypoint", outcome);
            return outcome;
        }
        catch (Exception error) { return "Rejected · " + Short(error.InnerException?.Message ?? error.Message); }
    }

    private void CompleteUrgent()
    {
        if (urgent is not { Task.IsCompleted: true } work) return;
        urgent = null;
        using CancellationTokenSource cancellation = work.Cancellation;
        try
        {
            JObject answer = work.Task.GetAwaiter().GetResult();
            string? choice = (string?)answer["optionId"];
            if (choice is not ("hold" or "limited-salvo")) throw new InvalidDataException("The provider returned an ineligible preview option.");
            string reason = Short((string?)answer["reason"]);
            if (reason.Length == 0 && (double?)answer["confidence"] is { } confidence) reason = $"Provider confidence: {confidence:P0}";
            Add("Urgent preview", $"{(choice == "hold" ? "Hold fire" : "Limited salvo")} · {work.Clock.Elapsed.TotalSeconds:0.0}s",
                reason, work.ShipId);
        }
        catch (Exception error) { Add("Urgent preview", "Failed", error.Message, work.ShipId); }
    }

    private void Add(string stage, string status, string detail, string? shipId = null, string? shipName = null,
        string? roeWarning = null, string? targetId = null)
    {
        if (Mode == ControlMode.AiControl && shipId != null && IsAppliedUnitOrder(stage, status)) CameraUnitId = shipId;
        if (events.Count == 90) events.Dequeue();
        events.Enqueue(new(GameTime.missionElapsedTime, stage, status, Short(detail, 900), shipId, shipName, roeWarning, targetId));
        Revision++;
        UnityEngine.Debug.Log($"[OODA] {stage} | {status}{(shipId == null ? "" : $" | {shipId}")} | {Short(detail, 900)}"
            + (roeWarning == null ? "" : $" | Possibly illegal fire: {roeWarning}"));
    }

    private static bool IsAppliedUnitOrder(string stage, string status) => stage switch
    {
        "Air operations" => status.Contains(" ordered", StringComparison.Ordinal) || status.StartsWith("Transport route resumed", StringComparison.Ordinal),
        "Flight Deck" or "Air defense" or "Surface defense" or "Maritime strike" or "Shore bombardment" => status.Contains(" queued", StringComparison.Ordinal),
        "Tactical" or "Approach" => status.StartsWith("Applied", StringComparison.Ordinal),
        _ => false
    };

    private static string Signature(JObject view) => string.Join("|", view["ownedUnits"]!.Concat(view["ownedOtherUnits"]!).Select(unit =>
        $"{unit["id"]}:{unit["order"]}:{unit["route"]}:{unit["weaponsPosture"]}")) + "|" + string.Join("|", view["contacts"]!.Select(track =>
        $"{track["id"]}:{track["posture"]}:{track["identified"]}:{track["live"]}")) + "|" + string.Join("|",
        view["airborne"]!.Select(flight => $"{flight["id"]}:{flight["leaderId"]}:{flight["order"]}:{flight["routeLoops"]}:{(int?)flight["fuelPercent"] / 10}:{flight["armedRoles"]}:{flight["engagedContacts"]}")
            .Concat(view["airWeaponFire"]!.Select(shot => $"{shot["unitId"]}:{shot["targetId"]}:{shot["weapon"]}:{shot["shots"]}"))
            .Concat(view["airTargets"]!.Select(track => $"{track["id"]}:{track["esm"]}"))
            .Concat(view["airDefenseAssessment"]!.Select(site => $"{site["targetId"]}:{site["assessment"]}:{site["liveFix"]}:{site["fixSource"]}:{site["missileInbound"]}"))
            .Concat(view["airborne"]!.Where(flight => flight["transportWindow"] is JObject).Select(flight =>
                $"{flight["id"]}:transport:{(double?)flight["transportWindow"]!["minutesOfHoldRemaining"] < 15}:{(double?)flight["transportWindow"]!["spareRangeNm"] <= 0}"))
            .Concat(view["heldTransportRouteWarnings"]!.Select(warning => $"{warning["unitId"]}:{warning["targetId"]}:{warning["assessment"]}"))
            .Append(TorpedoWindowSignature(view["submarines"]!))
            .Concat(view["coastalThreats"]!.Select(track => $"{track["targetId"]}:{track["engaged"]}:{track["nearFlightRoutes"]}"))
            .Concat(view["strafingOptions"]!.Select(option => $"{option["unitId"]}:{option["targetId"]}"))
            .Concat(view["airDecks"]!.SelectMany(deck => deck["fits"]?.Select(fit => $"{deck["shipId"]}:{fit["id"]}:{fit["availableCount"]}") ?? []))
            .Concat(view["airDecks"]!.SelectMany(deck => deck["deckTasks"]?.Select(task => $"{deck["shipId"]}:{task["loadout"]}:{task["state"]}:{task["launchAuthorized"]}") ?? [])));
    private static string TorpedoWindowSignature(JToken submarines) => string.Join("|", submarines.SelectMany(sub =>
        sub["torpedoTargets"]!.Select(target => $"{sub["shipId"]}:{target["targetId"]}:{target["immediateThreat"]}:{target["torpedoInbound"]}")
            .Concat(sub["stagingContacts"]!.Select(target => $"{sub["shipId"]}:{target["targetId"]}:{(double?)target["distanceToStandoffNm"] <= 0}"))));

    private static string RouteSignature(ObjectBase unit) => $"{unit.CurrentOrderText.Value}|{(unit is Submarine sub
        ? PlayerOrders.SubmarineRoute(sub) : PlayerPicture.Describe((Vessel)unit)["route"])}";
    private static string Stage(DecisionRole role) => role switch
    {
        DecisionRole.Operational => "Mission Command",
        DecisionRole.SurfaceDefense => "Surface defense",
        DecisionRole.AirDefense => "Air defense",
        _ => "Tactical"
    };
    private static string Short(string? value, int length = 350) => value is { Length: > 0 } ? value[..Math.Min(length, value.Length)].Replace('\r', ' ').Replace('\n', ' ') : "";

    private static void Cancel(ref Request? request)
    {
        if (request is not { } work) return;
        request = null;
        work.Cancellation.Cancel();
        _ = work.Task.ContinueWith(task => { _ = task.Exception; work.Cancellation.Dispose(); }, TaskScheduler.Default);
    }
}
