import { ActionManager } from "@babylonjs/core/Actions/actionManager.js";
import { ExecuteCodeAction } from "@babylonjs/core/Actions/directActions.js";
import { ArcRotateCamera } from "@babylonjs/core/Cameras/arcRotateCamera.js";
import "@babylonjs/core/Culling/ray.js";
import { Engine } from "@babylonjs/core/Engines/engine.js";
import { HemisphericLight } from "@babylonjs/core/Lights/hemisphericLight.js";
import { StandardMaterial } from "@babylonjs/core/Materials/standardMaterial.js";
import { Color3, Color4 } from "@babylonjs/core/Maths/math.color.js";
import { Vector3 } from "@babylonjs/core/Maths/math.vector.js";
import { Mesh } from "@babylonjs/core/Meshes/mesh.js";
import { MeshBuilder } from "@babylonjs/core/Meshes/meshBuilder.js";
import { Scene } from "@babylonjs/core/scene.js";
import { mesh as topologyMesh } from "topojson-client";
import worldLand from "world-atlas/land-110m.json";
import "./style.css";

type FriendlyEntity = {
  entityId: string;
  name: string;
  catalogUnitId: string;
  regionId: string;
  latitudeMicrodegrees: number;
  longitudeMicrodegrees: number;
  lifecycle: string;
  fidelity: string;
  revision: number;
  hostEntityId: string | null;
};

type FriendlyGroup = {
  groupId: string;
  name: string;
  memberEntityIds: string[];
  currentRegionId: string;
  activeOrderId: string | null;
  activeOrderKind: string | null;
  activeOrderDestinationRegionId: string | null;
  playerControlled: boolean;
  orderStatus: string;
  canMove: boolean;
  revision: number;
};

type FriendlyBase = {
  baseId: string;
  name: string;
  regionId: string;
  latitudeMicrodegrees: number;
  longitudeMicrodegrees: number;
};

type Contact = {
  trackId: string;
  estimatedRegionId: string;
  estimatedLatitudeMicrodegrees: number;
  estimatedLongitudeMicrodegrees: number;
  confidence: string;
  uncertaintyRadiusMeters: number;
  classification: string;
  observedAtQuanta: number;
  revision: number;
};

type Objective = {
  objectiveId: string;
  kind: string;
  regionId: string;
  regionName: string;
  priorityMilliUnits: number;
  assignedGroupCount: number;
  playerAssignedGroupCount: number;
};

type PlayerCampaign = {
  campaignId: string;
  name: string;
  scenarioId: string;
  saveSchemaVersion: number;
  algorithmVersion: number;
  catalogFingerprint: string;
  playerId: string;
  coalitionId: string;
  timeQuanta: number;
  tempo: {
    simulatedSecondsPerQuantum: number;
    advanceQuanta: number;
    wallIntervalMilliseconds: number;
    targetMultiplier: number;
  };
  head: number;
  paused: boolean;
  lastCursor: number;
  lastSavedHead: number | null;
  lastSavedAt: string | null;
  trustBoundary: string;
  scenarioEntityCount: number;
  friendlyEntities: FriendlyEntity[];
  friendlyGroups: FriendlyGroup[];
  friendlyBases: FriendlyBase[];
  contacts: Contact[];
  objectives: Objective[];
};

type PlayerOrderResult = { status: string; orderId: string; campaign: PlayerCampaign };

type TacticalEntryMember = {
  entityId: string;
  name: string;
  catalogUnitId: string;
  status: string;
  message: string;
  hostEntityId: string | null;
};

type TacticalEntry = {
  campaignId: string;
  groupId: string;
  groupName: string;
  groupRevision: number;
  state: string;
  message: string;
  workerConnected: boolean;
  canEnter: boolean;
  requiredMemberCount: number;
  supportedMemberCount: number;
  availableTacticalCapacity: number;
  workerClientInstanceId: string | null;
  playerTaskforceId: string | null;
  members: TacticalEntryMember[];
};

type TacticalEntryResult = { status: string; batchId: string; entry: TacticalEntry };

type TacticalResultMember = {
  entityId: string;
  name: string;
  lifecycle: string;
  fidelity: string;
  damageMilliunits: number;
  fuelMilliunits: number;
  storesMilliunits: number;
  assignmentLifecycle: string;
  checkpointSha256: string | null;
  relationship: string;
};

type TacticalExit = {
  campaignId: string;
  groupId: string;
  groupName: string;
  groupRevision: number;
  state: string;
  message: string;
  canExit: boolean;
  batchId: string | null;
  campaignTimeQuanta: number;
  members: TacticalResultMember[];
  unsupportedFields: string[];
};

type TacticalExitResult = { status: string; exit: TacticalExit };

type PlayerCampaignSummary = {
  campaignId: string;
  name: string;
  commitPosition: number;
  catalogFingerprint: string;
  playerId: string;
  coalitionId: string | null;
  paused: boolean;
  lastCursor: number;
  lastSavedHead: number | null;
  lastSavedAt: string | null;
};

type Selection =
  | { kind: "group"; id: string }
  | { kind: "friendly"; id: string }
  | { kind: "contact"; id: string }
  | { kind: "base"; id: string };

const app = document.querySelector<HTMLElement>("#app")!;
app.innerHTML = `
  <div class="command-shell">
    <canvas id="campaign-map" class="absolute inset-0 h-full w-full outline-none" aria-label="Coalition campaign map"></canvas>
    <header class="instrument-strip flex items-center justify-between px-3">
      <div class="flex min-w-0 items-center gap-3">
        <div class="border border-[#666] px-2 py-1 text-[9px] tracking-[0.16em] text-[#e2e2e2]">CRIMEAN TOM</div>
        <div class="min-w-0"><div class="metric-label">Local dynamic campaign</div><h1 id="campaign-title" class="truncate text-[11px] font-normal uppercase text-[#e2e2e2]">No campaign open</h1></div>
      </div>
      <div class="flex h-full items-center text-right">
        <div><div class="metric-label">Campaign time</div><div id="campaign-time" class="metric-value">PAUSED</div></div>
        <div class="ml-5 border-l border-[#252525] pl-5"><div class="metric-label">Save head</div><div id="save-head" class="metric-value">—</div></div>
        <div class="ml-5 flex items-center gap-2 border-l border-[#252525] pl-5"><span id="health-dot" class="status-mark warning"></span><span id="health-label" class="text-[10px] uppercase">Starting</span></div>
      </div>
    </header>

    <div class="map-annotation map-annotation-left">COALITION PICTURE // FRIENDLIES AND KNOWN CONTACTS ONLY</div>
    <div class="map-annotation map-annotation-right">DRAG TO ORBIT // WHEEL TO ZOOM // SELECT A SYMBOL</div>

    <aside class="dock dock-left">
      <div class="window-title flex items-center justify-between px-2"><span>CAMPAIGN</span><span class="text-[#626262]">LOCAL PLAYER</span></div>
      <div class="campaign-link-panel grid gap-1 p-2">
        <label class="metric-label" for="campaign-picker">Saved campaigns</label>
        <select id="campaign-picker" class="command-input campaign-picker" aria-label="Saved campaigns"><option value="">LOADING…</option></select>
        <button id="open-campaign" class="command-cell command-cell-primary">OPEN CAMPAIGN</button>
        <div class="section-rule my-1"></div>
        <label class="metric-label" for="campaign-name">New campaign name</label>
        <input id="campaign-name" class="command-input" maxlength="80" value="World War III, 1989" />
        <label class="metric-label" for="campaign-coalition">Player coalition</label>
        <select id="campaign-coalition" class="command-input"><option value="Blue">NATO AND ALIGNED</option><option value="Red">WARSAW PACT AND ALIGNED</option></select>
        <button id="create-campaign" class="command-cell">CREATE 4,096-ENTITY CAMPAIGN</button>
      </div>
      <div class="window-title flex items-center justify-between border-t border-[#3b3b3b] px-2"><span>TIME AND SAVE</span><span id="run-state" class="text-[#e2e2e2]">PAUSED</span></div>
      <div class="grid grid-cols-3 gap-1 p-2">
        <button id="resume-campaign" class="command-cell command-cell-primary" disabled>START</button>
        <button id="pause-campaign" class="command-cell" disabled>PAUSE</button>
        <button id="save-campaign" class="command-cell" disabled>SAVE</button>
      </div>
      <div id="connection-message" class="section-rule min-h-8 px-2 py-2 text-[9px] text-[#a8a8a8]">CONNECTING TO LOCAL CAMPAIGN SERVICE</div>
      <div class="window-title flex items-center justify-between px-2"><span>FRIENDLY FORCES</span><span id="unit-count" class="text-[#e2e2e2]">0 UNITS</span></div>
      <div id="unit-list" class="scrollbar unit-tree min-h-0 flex-1 overflow-y-auto" role="tree" aria-label="Friendly force hierarchy"></div>
    </aside>

    <aside class="dock dock-right scrollbar overflow-y-auto">
      <div class="window-title flex items-center justify-between px-2"><span>SELECTED</span><span id="selection-kind">NONE</span></div>
      <div id="selection"><div class="px-2 py-3 text-[10px] text-[#626262]">SELECT A FRIENDLY, BASE, OR KNOWN CONTACT</div></div>
      <div class="window-title flex items-center justify-between border-t border-[#3b3b3b] px-2"><span>PLAYER ORDER</span><span id="order-state">NO GROUP</span></div>
      <div id="order-panel" class="p-2 text-[9px] text-[#626262]">SELECT A FRIENDLY GROUP</div>
      <div class="window-title flex items-center justify-between border-t border-[#3b3b3b] px-2"><span>TACTICAL ENTRY</span><span id="tactical-state">NO GROUP</span></div>
      <div id="tactical-panel" class="p-2 text-[9px] text-[#626262]">SELECT A FRIENDLY GROUP</div>
      <div class="window-title flex items-center justify-between border-t border-[#3b3b3b] px-2"><span>OBJECTIVES</span><span id="objective-count">0</span></div>
      <div id="objective-list" class="max-h-44 overflow-y-auto"></div>
      <div class="window-title flex items-center justify-between border-t border-[#3b3b3b] px-2"><span>CAMPAIGN STATUS</span><span id="scenario-id">WW3++</span></div>
      <div class="state-bank">
        <div class="state-cell"><div class="metric-label">Campaign entities</div><div id="entity-count" class="metric-value">0</div></div>
        <div class="state-cell"><div class="metric-label">Friendly</div><div id="friendly-count" class="metric-value">0</div></div>
        <div class="state-cell"><div class="metric-label">Known contacts</div><div id="contact-count" class="metric-value">0</div></div>
        <div class="state-cell"><div class="metric-label">Friendly bases</div><div id="base-count" class="metric-value">0</div></div>
        <div class="state-cell"><div class="metric-label">Schema</div><div id="schema-version" class="metric-value">—</div></div>
        <div class="state-cell"><div class="metric-label">Algorithm</div><div id="algorithm-version" class="metric-value">—</div></div>
      </div>
      <div class="section-rule px-2 py-2 text-[9px] text-[#626262]" id="save-status">NO CAMPAIGN SAVE LOADED</div>
    </aside>

    <footer class="command-footer flex items-center px-2 text-[8px] tracking-[0.07em] text-[#626262]">
      <span class="friendly mr-5">□ FRIENDLY FORCE</span>
      <span class="hostile mr-5">△ KNOWN CONTACT</span>
      <span class="warning mr-5">◇ FRIENDLY BASE</span>
      <span class="ml-auto">ENEMY TRUE STATE IS NOT SENT TO THIS APPLICATION</span>
    </footer>
  </div>`;

const byId = <T extends HTMLElement>(id: string) => document.querySelector<T>(`#${id}`)!;
const canvas = byId<HTMLCanvasElement>("campaign-map");
const engine = new Engine(canvas, true, { preserveDrawingBuffer: true, stencil: true });
const scene = new Scene(engine);
scene.clearColor = new Color4(0.008, 0.008, 0.008, 1);
const camera = new ArcRotateCamera("camera", -Math.PI / 2, 0.82, 74, Vector3.Zero(), scene);
camera.lowerRadiusLimit = 8;
camera.upperRadiusLimit = 135;
camera.attachControl(canvas, true);
new HemisphericLight("map-light", new Vector3(0.2, 1, -0.3), scene).intensity = 0.4;

const gridLines: Vector3[][] = [];
for (let longitude = -180; longitude <= 180; longitude += 15) gridLines.push([worldPosition(longitude, -85), worldPosition(longitude, 85)]);
for (let latitude = -75; latitude <= 75; latitude += 15) gridLines.push([worldPosition(-180, latitude), worldPosition(180, latitude)]);
const plotGrid = MeshBuilder.CreateLineSystem("plot-grid", { lines: gridLines }, scene);
plotGrid.color = new Color3(0.16, 0.16, 0.16);
plotGrid.alpha = 0.34;
plotGrid.isPickable = false;
const topology = worldLand as unknown as Parameters<typeof topologyMesh>[0];
const coastlineGeometry = topologyMesh(topology, topology.objects.land);
const coastlineLines = coastlineGeometry.coordinates.flatMap(coordinates => splitDateLine(coordinates)
  .filter(segment => segment.length > 1)
  .map(segment => segment.map(([longitude, latitude]) => worldPosition(longitude, latitude, 0.02))));
const coastline = MeshBuilder.CreateLineSystem("world-coastline", { lines: coastlineLines }, scene);
coastline.color = new Color3(0.56, 0.56, 0.56);
coastline.alpha = 0.72;
coastline.isPickable = false;

let campaign: PlayerCampaign | null = null;
let dynamicMeshes: Mesh[] = [];
let selection: Selection | null = null;
let orderGroupId = "";
let destinationRegionId = "";
let orderMessage = "SELECT A GROUP AND DESTINATION";
let tacticalEntry: TacticalEntry | null = null;
let tacticalExit: TacticalExit | null = null;
let tacticalLoading = false;
let tacticalMessage = "SELECT A GROUP TO CHECK SEA POWER";
let pollHandle: number | undefined;

function worldPosition(longitude: number, latitude: number, y = 0): Vector3 {
  return new Vector3(longitude / 3, y, latitude / 3);
}

function mapPosition(latitude: number, longitude: number, y = 0): Vector3 {
  return worldPosition(longitude / 1_000_000, latitude / 1_000_000, y);
}

function splitDateLine(coordinates: number[][]): number[][][] {
  const segments: number[][][] = [[]];
  for (const coordinate of coordinates) {
    if (segments.at(-1)!.length && Math.abs(segments.at(-1)!.at(-1)![0] - coordinate[0]) > 180) segments.push([]);
    segments.at(-1)!.push(coordinate);
  }
  return segments;
}

function material(name: string, color: Color3): StandardMaterial {
  const value = new StandardMaterial(name, scene);
  value.diffuseColor = color;
  value.emissiveColor = color.scale(0.5);
  return value;
}

const friendlyMaterial = material("friendly-material", new Color3(0.21, 0.91, 0.54));
const contactMaterial = material("contact-material", new Color3(1, 0.25, 0.32));
const baseMaterial = material("base-material", new Color3(0.94, 0.7, 0.2));

function selectable(mesh: Mesh, next: Selection): void {
  mesh.isPickable = true;
  mesh.actionManager = new ActionManager(scene);
  mesh.actionManager.registerAction(new ExecuteCodeAction(ActionManager.OnPickTrigger, () => select(next)));
}

function renderMap(data: PlayerCampaign): void {
  dynamicMeshes.forEach(mesh => mesh.dispose());
  dynamicMeshes = [];
  for (const entity of data.friendlyEntities) {
    const mesh = MeshBuilder.CreateBox(`friendly:${entity.entityId}`, { size: 0.34 }, scene);
    mesh.position = mapPosition(entity.latitudeMicrodegrees, entity.longitudeMicrodegrees, 0.25);
    mesh.material = friendlyMaterial;
    selectable(mesh, { kind: "friendly", id: entity.entityId });
    dynamicMeshes.push(mesh);
  }
  for (const contact of data.contacts) {
    const mesh = MeshBuilder.CreateCylinder(`contact:${contact.trackId}`, { diameterTop: 0, diameterBottom: 0.42, height: 0.5, tessellation: 3 }, scene);
    mesh.position = mapPosition(contact.estimatedLatitudeMicrodegrees, contact.estimatedLongitudeMicrodegrees, 0.3);
    mesh.material = contactMaterial;
    selectable(mesh, { kind: "contact", id: contact.trackId });
    dynamicMeshes.push(mesh);
  }
  for (const base of data.friendlyBases) {
    const mesh = MeshBuilder.CreateCylinder(`base:${base.baseId}`, { diameter: 0.32, height: 0.12, tessellation: 4 }, scene);
    mesh.position = mapPosition(base.latitudeMicrodegrees, base.longitudeMicrodegrees, 0.12);
    mesh.material = baseMaterial;
    selectable(mesh, { kind: "base", id: base.baseId });
    dynamicMeshes.push(mesh);
  }
}

function renderCampaign(data: PlayerCampaign): void {
  campaign = data;
  byId("campaign-title").textContent = data.name;
  byId("campaign-time").textContent =
    `${formatCampaignTime(data)} // ${formatTempo(data.tempo.targetMultiplier)}`;
  byId("save-head").textContent = data.lastSavedHead === null ? "UNSAVED" : `C${data.lastSavedHead}`;
  byId("run-state").textContent = data.paused ? "PAUSED" : "RUNNING";
  byId("scenario-id").textContent = data.scenarioId.toUpperCase();
  byId("entity-count").textContent = data.scenarioEntityCount.toLocaleString();
  byId("friendly-count").textContent = data.friendlyEntities.length.toLocaleString();
  byId("contact-count").textContent = data.contacts.length.toLocaleString();
  byId("base-count").textContent = data.friendlyBases.length.toLocaleString();
  byId("schema-version").textContent = `V${data.saveSchemaVersion}`;
  byId("algorithm-version").textContent = `V${data.algorithmVersion}`;
  byId<HTMLButtonElement>("resume-campaign").disabled = !data.paused;
  byId<HTMLButtonElement>("resume-campaign").textContent = data.timeQuanta === 0 ? "START" : "RESUME";
  byId<HTMLButtonElement>("pause-campaign").disabled = data.paused;
  byId<HTMLButtonElement>("save-campaign").disabled = false;
  byId("save-status").textContent = data.lastSavedAt
    ? `SAVED C${data.lastSavedHead} // ${new Date(data.lastSavedAt).toLocaleString()}`
    : "NO EXPLICIT SAVE YET";
  byId("connection-message").textContent = `${data.paused ? "PAUSED" : "RUNNING"} // ${data.trustBoundary.toUpperCase()}`;
  byId("unit-count").textContent = `${data.friendlyEntities.length.toLocaleString()} UNITS`;
  renderForceHierarchy(data);
  renderMap(data);
  renderObjectives(data);
  if (selection) select(selection);
  else {
    renderOrderPanel();
    renderTacticalPanel();
  }
  void refreshTacticalEntry().catch(showTacticalError);
}

function formatCampaignTime(data: PlayerCampaign): string {
  const seconds = Math.floor(data.timeQuanta * data.tempo.simulatedSecondsPerQuantum);
  const day = Math.floor(seconds / 86_400) + 1;
  const daySeconds = seconds % 86_400;
  const hours = Math.floor(daySeconds / 3_600);
  const minutes = Math.floor(daySeconds % 3_600 / 60);
  const remainder = daySeconds % 60;
  return `D${String(day).padStart(3, "0")} `
    + `${String(hours).padStart(2, "0")}:${String(minutes).padStart(2, "0")}:${String(remainder).padStart(2, "0")}`;
}

function formatTempo(multiplier: number): string {
  return `${Number.isInteger(multiplier) ? multiplier.toFixed(0) : multiplier.toFixed(1)}x`;
}

function renderForceHierarchy(data: PlayerCampaign): void {
  const entities = new Map(data.friendlyEntities.map(entity => [entity.entityId, entity]));
  byId("unit-list").innerHTML = data.friendlyGroups.map(group => `
    <section class="tree-group">
      <button class="tree-group-row w-full bg-transparent text-left ${group.groupId === orderGroupId ? "register-row-selected" : ""}" data-group-id="${group.groupId}"><span>${escapeHtml(group.name.toUpperCase())}</span><span>${escapeHtml(group.playerControlled ? group.orderStatus.toUpperCase() : String(group.memberEntityIds.length))}</span></button>
      ${group.memberEntityIds.map(id => entities.get(id)).filter((value): value is FriendlyEntity => Boolean(value)).map(entity => `
        <button class="tree-unit-row" data-entity-id="${entity.entityId}"><span class="friendly">□</span><span class="truncate">${escapeHtml(entity.name.toUpperCase())}</span><span>${escapeHtml(entity.fidelity.toUpperCase())}</span></button>`).join("")}
    </section>`).join("") || `<div class="px-2 py-3 text-[9px] text-[#626262]">NO FRIENDLY GROUPS</div>`;
  document.querySelectorAll<HTMLElement>("[data-group-id]").forEach(row => row.addEventListener("click", () => {
    orderGroupId = row.dataset.groupId!;
    select({ kind: "group", id: orderGroupId });
  }));
  document.querySelectorAll<HTMLElement>("[data-entity-id]").forEach(row => row.addEventListener("click", () => {
    const group = data.friendlyGroups.find(item => item.memberEntityIds.includes(row.dataset.entityId!));
    if (group) orderGroupId = group.groupId;
    select({ kind: "friendly", id: row.dataset.entityId! });
  }));
}

function select(next: Selection): void {
  if (!campaign) return;
  selection = next;
  if (next.kind === "group") {
    const group = campaign.friendlyGroups.find(item => item.groupId === next.id);
    if (!group) return;
    orderGroupId = group.groupId;
    byId("selection-kind").textContent = "GROUP";
    byId("selection").innerHTML = detail(group.name, `${group.memberEntityIds.length} FRIENDLY UNITS`, [
      ["Region", regionName(group.currentRegionId)],
      ["Order", group.activeOrderKind ?? "None"],
      ["Order state", group.orderStatus],
      ["Revision", String(group.revision)],
    ]);
  } else if (next.kind === "friendly") {
    const entity = campaign.friendlyEntities.find(item => item.entityId === next.id);
    if (!entity) return;
    byId("selection-kind").textContent = "FRIENDLY";
    byId("selection").innerHTML = detail(entity.name, entity.catalogUnitId, [
      ["Region", entity.regionId], ["Fidelity", entity.fidelity], ["State", entity.lifecycle], ["Revision", String(entity.revision)],
    ]) + destinationButton(entity.regionId, entity.name);
    bindDestinationButton(entity.regionId, entity.name);
  } else if (next.kind === "contact") {
    const contact = campaign.contacts.find(item => item.trackId === next.id);
    if (!contact) return;
    byId("selection-kind").textContent = "CONTACT";
    byId("selection").innerHTML = detail(contact.trackId, contact.classification, [
      ["Confidence", contact.confidence], ["Uncertainty", `${contact.uncertaintyRadiusMeters.toLocaleString()} m`], ["Observed", `T+${contact.observedAtQuanta}`],
    ]) + destinationButton(contact.estimatedRegionId, contact.classification);
    bindDestinationButton(contact.estimatedRegionId, contact.classification);
  } else {
    const base = campaign.friendlyBases.find(item => item.baseId === next.id);
    if (!base) return;
    byId("selection-kind").textContent = "BASE";
    byId("selection").innerHTML = detail(base.name, "FRIENDLY INSTALLATION", [["Region", regionName(base.regionId)]])
      + destinationButton(base.regionId, base.name);
    bindDestinationButton(base.regionId, base.name);
  }
  renderOrderPanel();
  renderTacticalPanel();
  void refreshTacticalEntry().catch(showTacticalError);
}

function destinationButton(regionId: string, label: string): string {
  return `<div class="p-2"><button id="set-order-destination" class="command-cell w-full">SET ${escapeHtml(label.toUpperCase())} AS DESTINATION</button><div class="mt-1 text-[8px] text-[#626262]">${escapeHtml(regionName(regionId).toUpperCase())}</div></div>`;
}

function bindDestinationButton(regionId: string, label: string): void {
  byId<HTMLButtonElement>("set-order-destination").addEventListener("click", () => {
    destinationRegionId = regionId;
    orderMessage = `${label.toUpperCase()} SELECTED`;
    renderOrderPanel();
  });
}

function regionName(regionId: string): string {
  if (!campaign) return regionId;
  return campaign.objectives.find(item => item.regionId === regionId)?.regionName
    ?? campaign.friendlyBases.find(item => item.regionId === regionId)?.name
    ?? regionId;
}

function renderObjectives(data: PlayerCampaign): void {
  byId("objective-count").textContent = String(data.objectives.length);
  byId("objective-list").innerHTML = data.objectives.map(objective => `
    <button class="register-row grid w-full grid-cols-[1fr_72px] gap-2 bg-transparent px-2 py-2 text-left text-[9px]" data-objective-region="${objective.regionId}">
      <span><span class="block text-[#e2e2e2]">${escapeHtml(objective.kind.toUpperCase())} // ${escapeHtml(objective.regionName.toUpperCase())}</span><span class="mt-1 block text-[8px] text-[#626262]">${objective.assignedGroupCount} ASSIGNED // ${objective.playerAssignedGroupCount} PLAYER</span></span>
      <span class="text-right text-[#626262]">${Math.round(objective.priorityMilliUnits / 1_000)}</span>
    </button>`).join("") || `<div class="px-2 py-3 text-[9px] text-[#626262]">OBJECTIVES APPEAR AFTER THE WAR STARTS</div>`;
  document.querySelectorAll<HTMLElement>("[data-objective-region]").forEach(row => row.addEventListener("click", () => {
    destinationRegionId = row.dataset.objectiveRegion!;
    orderMessage = `${regionName(destinationRegionId).toUpperCase()} OBJECTIVE SELECTED`;
    renderOrderPanel();
  }));
}

function renderOrderPanel(): void {
  if (!campaign) return;
  const group = campaign.friendlyGroups.find(item => item.groupId === orderGroupId);
  byId("order-state").textContent = group?.orderStatus.toUpperCase() ?? "NO GROUP";
  if (!group) {
    byId("order-panel").innerHTML = "SELECT A FRIENDLY GROUP";
    return;
  }
  const compatible = campaign.algorithmVersion === 19;
  const disabled = !group.canMove || !destinationRegionId || !compatible;
  const destination = destinationRegionId ? regionName(destinationRegionId) : "NOT SET";
  byId("order-panel").innerHTML = `
    <div class="detail-row"><span>GROUP</span><strong>${escapeHtml(group.name.toUpperCase())}</strong></div>
    <div class="detail-row"><span>DESTINATION</span><strong>${escapeHtml(destination.toUpperCase())}</strong></div>
    <div class="detail-row"><span>CURRENT</span><strong>${escapeHtml(group.orderStatus.toUpperCase())}</strong></div>
    <div class="mt-2 grid grid-cols-2 gap-1">
      <button id="issue-move-order" class="command-cell command-cell-primary" ${disabled ? "disabled" : ""}>ISSUE MOVE</button>
      <button id="cancel-order" class="command-cell" ${!group.canMove || !compatible ? "disabled" : ""}>CANCEL AND HOLD</button>
    </div>
    <div class="mt-2 text-[8px] text-[#626262]">${escapeHtml((compatible ? orderMessage : `ORDERS REQUIRE ALGORITHM 19, CURRENT ${campaign.algorithmVersion}`).toUpperCase())}</div>`;
  byId<HTMLButtonElement>("issue-move-order").addEventListener("click", () => void submitOrder(false).catch(showError));
  byId<HTMLButtonElement>("cancel-order").addEventListener("click", () => void submitOrder(true).catch(showError));
}

function renderTacticalPanel(): void {
  if (!campaign) return;
  const group = campaign.friendlyGroups.find(item => item.groupId === orderGroupId);
  if (!group) {
    byId("tactical-state").textContent = "NO GROUP";
    byId("tactical-panel").innerHTML = "SELECT A FRIENDLY GROUP";
    return;
  }

  const entry = tacticalEntry?.campaignId === campaign.campaignId && tacticalEntry.groupId === group.groupId
    ? tacticalEntry
    : null;
  const exit = tacticalExit?.campaignId === campaign.campaignId && tacticalExit.groupId === group.groupId
    ? tacticalExit
    : null;
  const currentEntry = entry?.groupRevision === group.revision ? entry : null;
  const currentExit = exit?.groupRevision === group.revision ? exit : null;
  const tacticalState = currentExit?.state === "active" || currentExit?.state === "leaving"
    || currentExit?.state === "recovering" || currentExit?.state === "blocked"
    ? currentExit.state
    : entry?.state ?? currentExit?.state ?? "unchecked";
  byId("tactical-state").textContent = tacticalLoading ? "CHECKING" : tacticalState.toUpperCase();
  const message = currentExit?.state === "active" || currentExit?.state === "leaving"
    ? currentExit.message
    : entry?.message ?? currentExit?.message ?? tacticalMessage;
  const members = entry?.members.map(member => `
    <div class="detail-row"><span class="truncate" title="${escapeHtml(member.catalogUnitId)}">${escapeHtml(member.name.toUpperCase())}</span><strong title="${escapeHtml(member.message)}">${escapeHtml(member.status.toUpperCase())}</strong></div>`).join("") ?? "";
  const capacity = entry ? `${entry.supportedMemberCount}/${entry.requiredMemberCount} SUPPORTED // ${entry.availableTacticalCapacity} SLOTS` : "NOT CHECKED";
  const results = currentExit?.members.map(member => `
    <div class="detail-row"><span class="truncate">${escapeHtml(member.name.toUpperCase())} // ${escapeHtml(member.relationship.toUpperCase())}</span>
    <strong>${escapeHtml(member.lifecycle.toUpperCase())} // ${Math.round(member.damageMilliunits / 10_000)}% DMG</strong></div>`).join("") ?? "";
  const unsupported = currentExit?.state === "complete" && currentExit.unsupportedFields.length
    ? `<div class="mt-1 text-[8px] text-[#626262]">NOT YET RETURNED // ${escapeHtml(currentExit.unsupportedFields.join(", ").toUpperCase())}</div>`
    : "";
  byId("tactical-panel").innerHTML = `
    <div class="detail-row"><span>GROUP</span><strong>${escapeHtml(group.name.toUpperCase())}</strong></div>
    <div class="detail-row"><span>SEA POWER</span><strong>${entry?.workerConnected ? "CONNECTED" : "NOT CONNECTED"}</strong></div>
    <div class="detail-row"><span>READINESS</span><strong>${escapeHtml(capacity)}</strong></div>
    <div class="mt-1 border-t border-[#252525] pt-1">${members || `<div class="py-1 text-[#626262]">MEMBER SUPPORT HAS NOT BEEN CHECKED</div>`}</div>
    ${results ? `<div class="mt-1 border-t border-[#252525] pt-1"><div class="metric-label mb-1">LAST COMMITTED RESULT // T+${currentExit?.campaignTimeQuanta}</div>${results}${unsupported}</div>` : ""}
    <div class="mt-2 grid grid-cols-3 gap-1">
      <button id="refresh-tactical-entry" class="command-cell" ${tacticalLoading ? "disabled" : ""}>CHECK SEA POWER</button>
      <button id="enter-tactical-session" class="command-cell command-cell-primary" ${!currentEntry?.canEnter || tacticalLoading ? "disabled" : ""}>ENTER SEA POWER</button>
      <button id="exit-tactical-session" class="command-cell" ${!currentExit?.canExit || tacticalLoading ? "disabled" : ""}>${currentExit?.state === "leaving" ? "FINALIZING" : "LEAVE SEA POWER"}</button>
    </div>
    <div class="mt-2 text-[8px] text-[#626262]">${escapeHtml(message.toUpperCase())}</div>`;
  byId<HTMLButtonElement>("refresh-tactical-entry").addEventListener("click", () => void refreshTacticalEntry().catch(showTacticalError));
  byId<HTMLButtonElement>("enter-tactical-session").addEventListener("click", () => void submitTacticalEntry().catch(showTacticalError));
  byId<HTMLButtonElement>("exit-tactical-session").addEventListener("click", () => void submitTacticalExit().catch(showTacticalError));
}

async function refreshTacticalEntry(): Promise<void> {
  if (!campaign || tacticalLoading) return;
  const activeCampaign = campaign;
  const group = activeCampaign.friendlyGroups.find(item => item.groupId === orderGroupId);
  if (!group) return;
  tacticalLoading = true;
  tacticalMessage = "CHECKING SEA POWER READINESS";
  renderTacticalPanel();
  try {
    const [entry, exit] = await Promise.all([
      request<TacticalEntry>(`/player/campaigns/${activeCampaign.campaignId}/groups/${group.groupId}/tactical-entry`),
      request<TacticalExit>(`/player/campaigns/${activeCampaign.campaignId}/groups/${group.groupId}/tactical-exit`),
    ]);
    if (campaign?.campaignId !== activeCampaign.campaignId || orderGroupId !== group.groupId) return;
    tacticalEntry = entry;
    tacticalExit = exit;
    tacticalMessage = entry.message;
  } finally {
    tacticalLoading = false;
    renderTacticalPanel();
  }
}

async function submitTacticalExit(): Promise<void> {
  if (!campaign || tacticalLoading) return;
  const activeCampaign = campaign;
  const group = activeCampaign.friendlyGroups.find(item => item.groupId === orderGroupId);
  const exit = tacticalExit?.campaignId === activeCampaign.campaignId && tacticalExit.groupId === group?.groupId
    ? tacticalExit
    : null;
  if (!group || !exit?.canExit || exit.groupRevision !== group.revision) return;
  tacticalLoading = true;
  tacticalMessage = "REQUESTING FINAL TACTICAL CHECKPOINTS";
  renderTacticalPanel();
  try {
    const route = `/player/campaigns/${activeCampaign.campaignId}/groups/${group.groupId}/tactical-exit`;
    const idempotencyKey = crypto.randomUUID();
    let candidate = exit;
    let result: TacticalExitResult | null = null;
    for (let attempt = 0; attempt < 2 && !result; attempt += 1) {
      try {
        result = await request<TacticalExitResult>(route, {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ expectedGroupRevision: candidate.groupRevision, idempotencyKey }),
        });
      } catch (error) {
        if (attempt !== 0 || !isGroupRevisionConflict(error)) throw error;
        candidate = await request<TacticalExit>(route);
        tacticalExit = candidate;
        if (!candidate.canExit) throw new Error(candidate.message);
      }
    }
    if (!result) throw new Error("TACTICAL EXIT DID NOT RETURN A RESULT");
    if (campaign?.campaignId !== activeCampaign.campaignId || orderGroupId !== group.groupId) return;
    tacticalExit = result.exit;
    tacticalMessage = result.exit.message;
  } finally {
    tacticalLoading = false;
    renderTacticalPanel();
  }
}

async function submitTacticalEntry(): Promise<void> {
  if (!campaign) return;
  const activeCampaign = campaign;
  const group = activeCampaign.friendlyGroups.find(item => item.groupId === orderGroupId);
  const entry = tacticalEntry?.campaignId === activeCampaign.campaignId && tacticalEntry.groupId === group?.groupId
    ? tacticalEntry
    : null;
  if (!group || !entry?.canEnter || entry.groupRevision !== group.revision) return;
  tacticalLoading = true;
  tacticalMessage = "PREPARING SELECTED GROUP";
  renderTacticalPanel();
  try {
    const route = `/player/campaigns/${activeCampaign.campaignId}/groups/${group.groupId}/tactical-entry`;
    const idempotencyKey = crypto.randomUUID();
    let candidate = entry;
    let result: TacticalEntryResult | null = null;
    for (let attempt = 0; attempt < 2 && !result; attempt += 1) {
      try {
        result = await request<TacticalEntryResult>(route, {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ expectedGroupRevision: candidate.groupRevision, idempotencyKey }),
        });
      } catch (error) {
        if (attempt !== 0 || !isGroupRevisionConflict(error)) throw error;
        candidate = await request<TacticalEntry>(route);
        tacticalEntry = candidate;
        if (!candidate.canEnter) throw new Error(candidate.message);
      }
    }
    if (!result) throw new Error("TACTICAL ENTRY DID NOT RETURN A RESULT");
    if (campaign?.campaignId !== activeCampaign.campaignId || orderGroupId !== group.groupId) return;
    tacticalEntry = result.entry;
    tacticalMessage = `${result.status.toUpperCase()} // BATCH ${result.batchId.slice(0, 8).toUpperCase()}`;
  } finally {
    tacticalLoading = false;
    renderTacticalPanel();
  }
}

function showTacticalError(error: unknown): void {
  tacticalLoading = false;
  tacticalMessage = error instanceof Error ? error.message : String(error);
  renderTacticalPanel();
}

async function submitOrder(cancel: boolean): Promise<void> {
  if (!campaign) return;
  const group = campaign.friendlyGroups.find(item => item.groupId === orderGroupId);
  if (!group) return;
  orderMessage = cancel ? "CANCELLING ORDER" : "SENDING MOVE ORDER";
  renderOrderPanel();
  const path = `/player/campaigns/${campaign.campaignId}/groups/${group.groupId}/orders/${cancel ? "cancel" : "move"}`;
  const body = cancel
    ? { expectedGroupRevision: group.revision, idempotencyKey: crypto.randomUUID() }
    : { destinationRegionId, expectedGroupRevision: group.revision, idempotencyKey: crypto.randomUUID() };
  const result = await request<PlayerOrderResult>(path, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(body),
  });
  orderMessage = `${result.status.toUpperCase()} // ORDER ${result.orderId.slice(0, 8).toUpperCase()}`;
  renderCampaign(result.campaign);
}

function detail(title: string, subtitle: string, rows: string[][]): string {
  return `<div class="p-2"><h2 class="text-[12px] text-[#e2e2e2]">${escapeHtml(title.toUpperCase())}</h2><div class="mb-3 text-[9px] text-[#626262]">${escapeHtml(subtitle.toUpperCase())}</div>${rows.map(([label, value]) => `<div class="detail-row"><span>${escapeHtml(label.toUpperCase())}</span><strong>${escapeHtml(value.toUpperCase())}</strong></div>`).join("")}</div>`;
}

class HttpRequestError extends Error {
  constructor(readonly status: number, readonly detail: string) {
    super(`${status} ${detail}`);
  }
}

function isGroupRevisionConflict(error: unknown): error is HttpRequestError {
  if (!(error instanceof HttpRequestError) || error.status !== 409) return false;
  try {
    return (JSON.parse(error.detail) as { error?: unknown }).error === "group-revision-conflict";
  } catch {
    return false;
  }
}

async function request<T>(path: string, init?: RequestInit, retried = false): Promise<T> {
  const response = await fetch(path, { credentials: "same-origin", ...init });
  if (response.status === 401 && !retried) {
    const renewed = await fetch("/monitor/session", { credentials: "same-origin" });
    if (renewed.ok) return request<T>(path, init, true);
  }
  if (!response.ok) throw new HttpRequestError(response.status, await response.text());
  return response.status === 204 ? undefined as T : response.json() as Promise<T>;
}

async function loadCampaigns(preferred = ""): Promise<PlayerCampaignSummary[]> {
  const campaigns = await request<PlayerCampaignSummary[]>("/player/campaigns");
  const picker = byId<HTMLSelectElement>("campaign-picker");
  picker.innerHTML = campaigns.length
    ? campaigns.map(item => `<option value="${item.campaignId}">${escapeHtml(item.name.toUpperCase())} // ${item.paused ? "PAUSED" : "RUNNING"} // C${item.commitPosition}</option>`).join("")
    : `<option value="">NO SAVED CAMPAIGNS</option>`;
  if (preferred && campaigns.some(item => item.campaignId === preferred)) picker.value = preferred;
  return campaigns;
}

async function openCampaign(id = byId<HTMLSelectElement>("campaign-picker").value): Promise<void> {
  if (!id) return;
  window.clearInterval(pollHandle);
  const data = await request<PlayerCampaign>(`/player/campaigns/${id}`);
  if (campaign?.campaignId !== data.campaignId) {
    selection = null;
    orderGroupId = "";
    destinationRegionId = "";
    orderMessage = "SELECT A GROUP AND DESTINATION";
    tacticalEntry = null;
    tacticalExit = null;
    tacticalMessage = "SELECT A GROUP TO CHECK SEA POWER";
  }
  renderCampaign(data);
  byId("health-dot").className = "status-mark friendly";
  byId("health-label").textContent = "READY";
  window.history.replaceState({}, "", `${window.location.pathname}?campaign=${id}`);
  pollHandle = window.setInterval(() => request<PlayerCampaign>(`/player/campaigns/${id}`).then(renderCampaign).catch(showError), 2_000);
}

async function createCampaign(): Promise<void> {
  const button = byId<HTMLButtonElement>("create-campaign");
  button.disabled = true;
  byId("connection-message").textContent = "CREATING AND SAVING THE 4,096-ENTITY CAMPAIGN";
  try {
    const created = await request<PlayerCampaign>("/player/campaigns", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ name: byId<HTMLInputElement>("campaign-name").value, coalition: byId<HTMLSelectElement>("campaign-coalition").value }),
    });
    await loadCampaigns(created.campaignId);
    byId<HTMLSelectElement>("campaign-picker").value = created.campaignId;
    await openCampaign(created.campaignId);
  } finally {
    button.disabled = false;
  }
}

async function mutate(action: "pause" | "resume" | "save"): Promise<void> {
  if (!campaign) return;
  await request(`/player/campaigns/${campaign.campaignId}/${action}`, { method: "POST" });
  await openCampaign(campaign.campaignId);
}

function showError(error: unknown): void {
  byId("health-dot").className = "status-mark hostile";
  byId("health-label").textContent = "ERROR";
  byId("connection-message").textContent = error instanceof Error ? error.message : String(error);
}

function escapeHtml(value: string): string {
  return value.replace(/[&<>'"]/g, character => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", "'": "&#39;", '"': "&quot;" })[character]!);
}

byId("open-campaign").addEventListener("click", () => void openCampaign().catch(showError));
byId("create-campaign").addEventListener("click", () => void createCampaign().catch(showError));
byId("resume-campaign").addEventListener("click", () => void mutate("resume").catch(showError));
byId("pause-campaign").addEventListener("click", () => void mutate("pause").catch(showError));
byId("save-campaign").addEventListener("click", () => void mutate("save").catch(showError));
window.addEventListener("resize", () => engine.resize());
engine.runRenderLoop(() => scene.render());

void (async () => {
  await request("/player/status");
  const linked = new URLSearchParams(window.location.search).get("campaign") ?? "";
  const campaigns = await loadCampaigns(linked);
  const target = campaigns.find(item => item.campaignId === linked)?.campaignId ?? campaigns[0]?.campaignId;
  if (target) await openCampaign(target);
  else {
    byId("health-dot").className = "status-mark friendly";
    byId("health-label").textContent = "READY";
    byId("connection-message").textContent = "CREATE A CAMPAIGN TO BEGIN";
  }
})().catch(showError);
