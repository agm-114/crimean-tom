using System.Diagnostics;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SeaPower;
using SeaPower.GameBridge;

namespace OODA;

internal enum ControlMode { Manual, Observe, AiControl }

internal record DecisionEvent(double Time, string Stage, string Status, string Detail, string? ShipId, string? ShipName,
    string? RoeWarning, string? TargetId);

internal class DecisionController
{
    private record Request(DecisionRole Role, Task<JObject> Task, CancellationTokenSource Cancellation,
        Stopwatch Clock, ChartDestination[] Destinations, Dictionary<string, string> Routes, string? ShipId);
    private record DeferredFire(JObject Order, string TargetId, double WallDeadline, double MissionDeadline);

    private readonly IDecisionProvider codex = new CodexDecisions();
    private readonly IDecisionProvider urgentProvider;
    private readonly DecisionClock clock = new();
    private readonly TimeInterrupts interrupts = new();
    private readonly TimeObservations timeObservations = new();
    private readonly SurfaceDefense defense = new();
    private readonly PlayerChart chart = new();
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
    private double nextTactical;
    private double nextOperational;
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
    internal int Revision { get; private set; }
    internal string Objective => (string?)plan?["objective"] ?? "No plan yet";
    internal string Provider => $"Codex {CodexDecisions.Model} · urgent: {(urgentProvider == codex ? "Codex mock" : "OpenAI Decisions")}";
    internal string Status => regular is { } request ? request.Task.IsCompleted ? $"{Stage(request.Role)} reply ready"
        : $"{Stage(request.Role)} pending · {request.Clock.Elapsed.TotalSeconds:0}s"
        : urgent is { } preview ? $"Urgent preview pending · {preview.Clock.Elapsed.TotalSeconds:0}s" : statusMessage ?? Mode switch
        { ControlMode.Observe => "Observing · orders are suggestions", ControlMode.AiControl => "AI control", _ => "Manual control" };
    internal JObject? LastInput { get; private set; }
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
    private bool AssessingAttack => defense.ActiveGunfire || defense.Pending || regular?.Role == DecisionRole.SurfaceDefense;
    private bool ManagingClock => Mode == ControlMode.AiControl && ManageTime;
    private (int Speed, string Source, string Reason)? DecisionPace => regular switch
    {
        { Role: DecisionRole.SurfaceDefense } request when request.Clock.Elapsed.TotalSeconds < 12 =>
            (2, "Surface defense", "Waiting briefly for a defensive fire decision."),
        { Role: DecisionRole.Operational } request when request.Clock.Elapsed.TotalSeconds < 20
            && request.Destinations.Any(point => point.Id.StartsWith("screen-", StringComparison.Ordinal))
            && plan?["assignments"]?.Any(item => (string?)item["task"] == "Screen") != true =>
            (10, "Escort planning", "Waiting briefly for the first escort waypoint."),
        _ => null
    };

    internal void TickTime(double now)
    {
        if (mission == null || PlayerPicture.Side is not { } side || !PlayerTime.Available) return;
        if (Mode == ControlMode.AiControl && !PlayerOrders.Blocked) RetrySurfaceOrders(side);
        interrupts.Expire(now);
        if (Mode != ControlMode.Manual)
        {
            foreach (TimeEvent item in timeObservations.Poll(side)) ObserveTimeEvent(item, now);
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
        else PlayerCombat.TakeMissileImpacts();
        clock.Tick(side, Mode == ControlMode.AiControl, plan != null, interrupts.Current, DecisionPace);
    }

    private void ObserveTimeEvent(TimeEvent item, double now)
    {
        if (interrupts.Observe(item, now, ManagingClock) is { } rule)
            Add("Time control", $"Interrupt · {rule.Speed}× for {rule.HoldSeconds}s", item.Detail, item.SubjectId, item.Label);
        if (item.Kind == TimeEventKind.MissileHit && (defense.ActiveGunfire || defense.HasNearbyThreat)) defense.Pending = true;
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
        deferredFire.Clear();
        wasAssessingAttack = false;
        timedAttacker = null;
        if (mode != ControlMode.AiControl)
        {
            chart.Clear();
            interrupts.CancelActive();
        }
        if (mode == ControlMode.Manual) timeObservations.Reset();
        clock.Tick(PlayerPicture.Side, mode == ControlMode.AiControl, plan != null, interrupts.Current);
        statusMessage = null;
        replan = mode != ControlMode.Manual;
        Add("Control", "Changed", mode.ToString());
    }

    internal void Reset()
    {
        Cancel(ref regular);
        Cancel(ref urgent);
        mission = null;
        Mode = ControlMode.Manual;
        clock.Reset();
        interrupts.Reset();
        timeObservations.Reset();
        defense.Reset();
        deferredFire.Clear();
        chart.Clear();
        marks = [];
        plan = LastInput = null;
        LastUrgentInput = null;
        UrgentShip = null;
        chartPoint = null;
        Briefing = previousSituation = objectiveState = statusMessage = null;
        nextTactical = nextOperational = urgentPreviews = 0;
        receivedMessages = 0;
        lookupUsed = replan = reviewingFire = false;
        wasAssessingAttack = false;
        timedAttacker = null;
        airDefensePending = false;
        raidWarningAt = double.NegativeInfinity;
        raidTracks.Clear();
        loggedTimeSuggestions.Clear();
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
            if (Mode == ControlMode.AiControl && !PlayerOrders.Blocked) RespondToObservedGunfire(side);
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
        if (regular is { } work)
        {
            if (!work.Task.IsCompleted) return;
            if (work.Role != DecisionRole.Operational && work.Task.Status == TaskStatus.RanToCompletion && Mode == ControlMode.AiControl)
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
            return;
        }
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

        ChartDestination[] destinations = NavigationOrders.Destinations(side, ships, chartPoint);
        string situation = Signature(view);
        nextTactical = now + 60;
        if (!replan && situation == previousSituation) return;
        previousSituation = situation;
        DecisionRole role = replan ? DecisionRole.Operational : DecisionRole.RoutineTactical;
        replan = false;
        if (role == DecisionRole.Operational) { lookupUsed = false; nextOperational = now + 60; }
        Start(role, view, ships, destinations);
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
        ["chartPoint"] = chartPoint?.Label, ["briefing"] = Briefing,
        ["plan"] = plan?.DeepClone(), ["lastInput"] = LastInput?.DeepClone(), ["missionMessages"] = MissionCommunications.Snapshot(),
        ["planMarks"] = PlanMarks(),
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

    private void Start(DecisionRole role, JObject view, Vessel[] ships, ChartDestination[] destinations)
    {
        view["plan"] = plan?.DeepClone();
        view["missionMessages"] = MissionCommunications.Snapshot();
        if (role is DecisionRole.Operational or DecisionRole.RoutineTactical)
        {
            if (PlayerPicture.Side is { } helicopterSide)
            {
                view["helicopterDecks"] = PlayerFlightDeck.Helicopters(helicopterSide, ships);
                view["helicopterScreenAnchors"] = new JArray(SurfaceDefense.ProtectedContacts(helicopterSide).Select(track => new JObject
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
        if (role is DecisionRole.SurfaceDefense or DecisionRole.AirDefense && PlayerPicture.Side is { } defensiveSide)
            SurfaceDefense.Describe(defensiveSide, view);
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
        view["availableTools"] = role == DecisionRole.Operational && !lookupUsed ? new JArray(new JObject
        {
            ["name"] = "get_owned_unit", ["requestField"] = "inspectGroupId", ["description"] = "Set inspectGroupId to an owned group ID for its heading and extended route, or null to submit the plan."
        }) : new JArray();
        view["lastOrderResults"] = new JArray(events.Where(item => item.ShipId != null && item.Stage is "Tactical" or "Surface defense" or "Air defense")
            .Reverse().Take(8).Select(item => new JObject
            {
                ["groupId"] = item.ShipId, ["shipName"] = item.ShipName, ["targetId"] = item.TargetId,
                ["result"] = item.Status, ["reason"] = item.Detail, ["roeWarning"] = item.RoeWarning
            }));
        DecisionPrompt prompt = DecisionPrompt.ForMission(role, view);
        Briefing = (string?)prompt.Input["briefing"];
        statusMessage = null;
        LastInput = prompt.Input;
        if (Mode == ControlMode.AiControl && role == DecisionRole.Operational && plan == null) clock.PauseForOpeningPlan();
        CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(60));
        regular = new(role, codex.Decide(role, prompt, cancellation.Token), cancellation, Stopwatch.StartNew(), destinations,
            ships.ToDictionary(PlayerPicture.Id, RouteSignature), null);
        int ownUnits = ships.Length + (view["ownedOtherUnits"]?.Count() ?? 0);
        Add(Stage(role), "Requested", $"{ownUnits} player units · {view["contacts"]?.Count() ?? 0} plotted contacts");
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
        if (work.Role == DecisionRole.Operational)
        {
            if (!lookupUsed && (string?)result["inspectGroupId"] is { } requested && groups.TryGetValue(requested, out Vessel ship))
            {
                lookupUsed = true;
                JObject view = PlayerPicture.Read(side, ships);
                view["toolResults"] = new JObject { ["tool"] = "get_owned_unit", ["unit"] = PlayerPicture.Inspect(ship) };
                Add("Mission Command", "Tool result", $"Read player-visible details for {ship.getName()}", requested);
                Start(work.Role, view, ships, NavigationOrders.Destinations(side, ships, chartPoint));
                return;
            }
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
            Add("Mission Command", $"Plan received · {work.Clock.Elapsed.TotalSeconds:0.0}s", objective);
            ApplyHelicopterOrders(result["helicopterOrders"], side, ships);
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
            reviewingFire = false;
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
        ApplyHelicopterOrders(result["helicopterOrders"], side, ships);
        Add("Tactical", $"Reply received · {work.Clock.Elapsed.TotalSeconds:0.0}s", $"{handled.Count} group decisions");
        if (Mode == ControlMode.AiControl && !replan && (int?)result["timeCompression"] is { } speed
            && LastInput?["availableTimeCompressions"] is JArray offered && offered.Values<int>().Contains(speed)
            && clock.Apply(speed, offered.Values<int>().Max(), Short((string?)result["timeReason"]), side, interrupts.Current))
            Add("Time control", $"Set {GameTime.TimeCompression:0.##}×", Short((string?)result["timeReason"]));
    }

    private void ApplyHelicopterOrders(JToken? proposed, Taskforce side, Vessel[] ships)
    {
        if (proposed is not JArray orders || LastInput?["helicopterDecks"] is not JArray decks) return;
        HashSet<string> assigned = [];
        foreach (JObject order in orders.OfType<JObject>().Take(4))
        {
            string? shipId = (string?)order["shipId"];
            if (shipId == null || !decks.Any(deck => (string?)deck["shipId"] == shipId) || !assigned.Add(shipId)
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
            string outcome = Mode == ControlMode.AiControl
                ? PlayerFlightDeck.QueueHelicopterScreen(host, side, route, (bool?)order["useRadar"] == true) : "Helicopter screen suggested";
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

    private void ApplySurfaceOrder(JObject order, Taskforce side, Vessel ship, string shipId, bool allowDeferred = true)
    {
        string reason = Short((string?)order["reason"]);
        string? targetId = (string?)order["targetId"];
        if (targetId == null || PlayerPicture.LiveContact(side, targetId) is not { BaseObject: Vessel } target)
        {
            double observedAt = (double?)LastInput?["missionClock"]?["elapsedSeconds"] ?? GameTime.missionElapsedTime;
            bool defer = allowDeferred && Mode == ControlMode.AiControl && targetId != null && LastInput?["contacts"] is JArray contacts
                && contacts.Any(track => (string?)track["id"] == targetId && (bool?)track["live"] == true);
            if (defer)
                deferredFire[shipId] = new(order, targetId!, UnityEngine.Time.realtimeSinceStartupAsDouble + 20,
                    GameTime.missionElapsedTime + 90);
            Add("Surface defense", defer ? "Deferred · lost target track" : "Rejected · no live surface target",
                $"{targetId ?? "No target"} · {GameTime.missionElapsedTime - observedAt:0}s since the decision snapshot · {reason}",
                shipId, ship.getName(), targetId: targetId);
            return;
        }
        if ((string?)order["weapons"] is not { } name || !Enum.TryParse(name, out SurfaceWeapon weapon) || weapon.ToString() != name)
        {
            Add("Surface defense", "Rejected · invalid weapon choice", reason, shipId, ship.getName(), targetId: targetId);
            return;
        }
        List<string> warnings = [];
        if (SurfaceDefense.ReviewEvidence(order, side, target) is { } evidenceWarning) warnings.Add(evidenceWarning);
        RelationsState relationship = target.CurrentRelationship();
        if (relationship is RelationsState.Friendly or RelationsState.Neutral) warnings.Add($"The target is plotted as {relationship}.");
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
            outcome = Mode == ControlMode.AiControl ? PlayerOrders.EngageSurface(ship, side, target, weapon) : new($"{weapon} suggested");
        }
        catch (Exception error) { outcome = new("Rejected", Short(error.Message)); }
        string detail = outcome.Detail.Length > 0 ? $"{targetName} · {outcome.Detail} · {reason}" : $"{targetName} · {reason}";
        Add("Surface defense", outcome.Status, detail, shipId, ship.getName(), warning, targetId);
        if (outcome.Status == "Guns unavailable") defense.ReviewGunFailure(targetId);
        if (warning != null)
        {
            reviewingFire = true;
            replan = true;
            nextOperational = 0;
        }
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
                || posture != RelationsState.Hostile && SurfaceDefense.ReviewEvidence(pending.Order, side, target) != null)
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
        try { return NavigationOrders.SetWaypoint(ship, side, destination); }
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
        if (events.Count == 90) events.Dequeue();
        events.Enqueue(new(GameTime.missionElapsedTime, stage, status, Short(detail, 900), shipId, shipName, roeWarning, targetId));
        Revision++;
        UnityEngine.Debug.Log($"[OODA] {stage} | {status}{(shipId == null ? "" : $" | {shipId}")} | {Short(detail, 900)}"
            + (roeWarning == null ? "" : $" | Possibly illegal fire: {roeWarning}"));
    }

    private static string Signature(JObject view) => string.Join("|", view["ownedUnits"]!.Concat(view["ownedOtherUnits"]!).Select(unit =>
        $"{unit["id"]}:{unit["order"]}:{unit["route"]}:{unit["weaponsPosture"]}")) + "|" + string.Join("|", view["contacts"]!.Select(track =>
        $"{track["id"]}:{track["posture"]}:{track["identified"]}:{track["live"]}"));
    private static string RouteSignature(Vessel ship) => $"{ship.CurrentOrderText.Value}|{PlayerPicture.Describe(ship)["route"]}";
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
