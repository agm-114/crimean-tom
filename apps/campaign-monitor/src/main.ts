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
import worldCountries from "world-atlas/countries-110m.json";
import worldLand from "world-atlas/land-110m.json";
import { countriesRepresentedInOrderOfBattle, type AlignedCountry, type Alignment } from "./alignedCountries";
import "./style.css";

type Region = { regionId: string; name: string; neighborRegionIds: string[] };
type Base = {
  baseId: string; name: string; coalitionId: string; regionId: string;
  latitudeMicrodegrees: number; longitudeMicrodegrees: number; resources: Record<string, number>;
};
type Entity = {
  entityId: string; name: string; catalogUnitId: string; catalogLoadoutId: string;
  operationalWeapons: OperationalWeapon[]; coalitionId: string; fidelity: string;
  simulationDomain: string; maximumFidelity: string; regionId: string;
  latitudeMicrodegrees: number; longitudeMicrodegrees: number; fuelMilliunits: number;
  storesMilliunits: number; damageMilliunits: number; lifecycle: string;
};
type OperationalWeapon = {
  ammunitionId: string; targetClassification: string; maximumRangeMeters: number;
};
type Group = {
  groupId: string; name: string; coalitionId: string; memberEntityIds: string[];
  activeOrderId: string | null;
};
type Outcome = {
  outcomeId: string; occurredAtQuanta: number; kind: string; regionId: string;
  actorEntityId: string; targetEntityId: string | null; munitionsExpendedMilliunits: number;
  damageInflictedMilliunits: number; detail: string;
};
type Assignment = {
  assignmentId: string; entityId: string; authorityEpoch: number; lifecycle: string;
  resolveByQuanta: number; revision: number; batchId: string; batchSize: number;
  workerClientInstanceId: string;
};
type Worker = {
  sessionId: string; clientInstanceId: string; gameSessionId: string; connectedAt: string;
  lastAppliedCommitPosition: number; receivedFrameCount: number;
  runtimeFingerprintSha256: string; catalogFingerprintSha256: string; playerTaskforceId: string;
  ownedMaterializedEntityCount: number; ownedActiveEntityCount: number;
  ownedRetainedSuspendedEntityCount: number; ownedRecoveryRequiredEntityCount: number;
  tacticalEntityCapacity: number; tacticalReserveCapacity: number;
  health: string; eligibility: string;
  occupiedTacticalEntityCount: number; promotionHeadroom: number; migrationOverlapHeadroom: number;
  materializationProfileCount: number; couplingRelationshipCount: number;
};
type InteractionIsland = {
  islandId: string; entityIds: string[]; groupIds: string[]; currentFidelity: string;
  desiredFidelity: string; engaged: boolean; couplingScoreMilliunits: number;
  unsafeToSplit: boolean; couplingReasons: string[]; reason: string;
};
type Scheduler = {
  health: string; eligibility: string; tacticalEntityCapacity: number; tacticalReserveCapacity: number;
  occupiedTacticalEntityCount: number; activeTacticalEntityCount: number;
  retainedSuspendedTacticalEntityCount: number; recoveryRequiredTacticalEntityCount: number;
  availableTacticalEntityCapacity: number; backlogDepth: number;
  stableSampleCount: number;
  fixedStepMicroseconds: number; targetTicksPerWallSecond: number; effectiveAtQuanta: number;
  loadAction: string; islands: InteractionIsland[];
};
type OperationTelemetry = {
  completedCount: number; lastDurationMicroseconds: number; maximumDurationMicroseconds: number;
  totalDurationMicroseconds: number; lastWorkUnits: number; totalWorkUnits: number;
  lastWorkUnitsPerSecondMilliunits: number;
};
type Performance = {
  serviceStartedAtUtc: string; serviceUptimeMilliseconds: number; managedMemoryBytes: number;
  workingSetBytes: number; recovery: OperationTelemetry; advance: OperationTelemetry;
  schedule: OperationTelemetry;
};
type Calibration = {
  schemaVersion: number; inEnvelope: boolean; maximumTacticalCompressionTimes: number;
  healthyTicksPerWallSecond: number; targetTicksPerWallSecond: number;
  observedWeaponExpenditureCount: number; observedRangeViolationCount: number;
  maximumRangeRatioMilliunits: number; envelopeMaximumRangeRatioMilliunits: number;
  observedFencedIslandCount: number; demotedFencedIslandCount: number; disagreements: string[];
};
type ReplicaEntity = {
  entityId: string; diagnosis: string; sampleCount: number; distinctCheckpointSha256: string[];
};
type ReplicaConvergence = {
  schemaVersion: number; majorityRule: string; entities: ReplicaEntity[];
};
type Campaign = {
  campaignId: string; name: string; commitPosition: number; timeQuanta: number; canonicalHash: string;
  regions: Region[]; bases: Base[]; entities: Entity[]; groups: Group[];
  assignments: Assignment[]; recentOperationalOutcomes: Outcome[]; scheduler: Scheduler;
  performance: Performance; workers: Worker[];
  calibration: Calibration;
  replicaConvergence: ReplicaConvergence;
};
type CampaignSummary = {
  campaignId: string; name: string; commitPosition: number; catalogFingerprint: string;
};

const app = document.querySelector<HTMLElement>("#app")!;
app.innerHTML = `
  <div class="command-shell">
    <canvas id="campaign-map" class="absolute inset-0 h-full w-full outline-none" aria-label="Interactive three-dimensional campaign map"></canvas>
    <header class="instrument-strip flex items-center justify-between px-3">
      <div class="flex min-w-0 items-center gap-3">
        <div class="border border-[#666] px-2 py-1 text-[9px] tracking-[0.16em] text-[#e2e2e2]">CT/OPCOM</div>
        <div class="min-w-0"><div class="metric-label">Theater command picture // dynamic campaign authority</div><h1 id="campaign-title" class="truncate text-[11px] font-normal uppercase text-[#e2e2e2]">Campaign monitor</h1></div>
      </div>
      <div class="flex h-full items-center gap-0 text-right">
        <div><div class="metric-label">Campaign time</div><div id="campaign-time" class="metric-value">—</div></div>
        <div class="ml-5 border-l border-[#252525] pl-5"><div class="metric-label">Commit</div><div id="commit-position" class="metric-value">—</div></div>
        <div class="ml-5 flex items-center gap-2 border-l border-[#252525] pl-5"><span id="health-dot" class="status-mark warning"></span><span id="health-label" class="text-[10px] uppercase">Disconnected</span></div>
      </div>
    </header>

    <div class="map-annotation map-annotation-left">PLOT / WORLD THEATER // GRID 15° // NATURAL EARTH 110M // ALIGNED BORDERS</div>
    <div class="map-annotation map-annotation-right">DRAG ORBIT // WHEEL RANGE // SELECT TRACK</div>

    <aside class="dock dock-left">
      <div class="window-title flex items-center justify-between px-2"><span>CAMPAIGN CHANNEL</span><span class="text-[#626262]">HOST / LOCAL</span></div>
      <div class="campaign-link-panel grid gap-1 p-2">
        <div class="mb-1 flex items-center gap-2 text-[9px]"><span class="status-mark friendly"></span><span>SESSION ATTACHED / NO TOKEN SETUP</span></div>
        <label class="metric-label" for="campaign-picker">Available campaigns</label>
        <select id="campaign-picker" class="command-input campaign-picker" aria-label="Available campaigns">
          <option value="">DISCOVERING CAMPAIGNS…</option>
        </select>
        <div class="campaign-address">
          <span class="metric-label">Direct link</span>
          <input id="campaign-id" class="campaign-id" aria-label="Campaign identifier" placeholder="PASTE CAMPAIGN ID" />
        </div>
        <div class="grid grid-cols-2 gap-1"><button id="connect" class="command-cell command-cell-primary">OPEN CHANNEL</button><button id="copy-link" class="command-cell">COPY LINK</button></div>
        <button id="bootstrap" class="command-cell">NEW FULDA GAP / 1989</button>
      </div>
      <div class="section-rule px-2 py-1">
        <div id="connection-message" class="min-h-4 truncate text-[9px] text-[#a8a8a8]">SESSION CREDENTIAL HELD BY HOST</div>
        <div id="worker-status" class="truncate text-[8px] uppercase tracking-[0.08em] text-[#626262]">WORKERS / UNAVAILABLE</div>
        <div id="scheduler-status" class="truncate text-[8px] uppercase tracking-[0.08em] text-[#626262]">SCHEDULER / UNAVAILABLE</div>
        <div id="performance-status" class="truncate text-[8px] uppercase tracking-[0.08em] text-[#626262]">PERF / UNAVAILABLE</div>
      </div>
      <div class="window-title flex items-center justify-between px-2"><span>ORDER OF BATTLE</span><span id="unit-count" class="text-[#e2e2e2]">00 UNITS</span></div>
      <div class="tree-columns border-b border-[#252525] px-2 py-1 text-[8px] text-[#626262]"><span>ECHELON / IDENTITY</span><span>FID</span></div>
      <div id="unit-list" class="scrollbar unit-tree min-h-0 flex-1 overflow-y-auto" role="tree" aria-label="Campaign order of battle"></div>
    </aside>

    <aside class="dock dock-right">
      <div class="window-title flex items-center justify-between px-2"><span>TRACK INSPECTOR</span><span>CANONICAL</span></div>
      <div id="selection"><div class="px-2 py-3 text-[10px] text-[#626262]">SELECT A WIRE TRACK OR FORCE-REGISTER ROW</div></div>
      <div class="window-title flex items-center justify-between border-t border-[#3b3b3b] px-2"><span>EVENT REGISTER</span><span id="outcome-count" class="text-[#e2e2e2]">00 EVENTS</span></div>
      <div class="grid grid-cols-[54px_76px_1fr] border-b border-[#252525] px-2 py-1 text-[8px] text-[#626262]"><span>TIME</span><span>TYPE</span><span>REPORT / EFFECT</span></div>
      <div id="outcome-list" class="scrollbar min-h-0 flex-1 overflow-y-auto" aria-live="polite"></div>
      <div class="state-bank">
        <div class="state-cell"><div class="metric-label">Regions</div><div id="region-count" class="metric-value">0</div></div>
        <div class="state-cell"><div class="metric-label">Groups</div><div id="group-count" class="metric-value">0</div></div>
        <div class="state-cell"><div class="metric-label">Tactical</div><div id="tactical-count" class="metric-value">0</div></div>
        <div class="state-cell"><div class="metric-label">Authority</div><div id="authority-count" class="metric-value">0</div></div>
        <div class="state-cell"><div class="metric-label">Workers</div><div id="worker-count" class="metric-value">0</div></div>
        <div class="state-cell"><div class="metric-label">Tempo</div><div id="tempo" class="metric-value">—</div></div>
        <div class="state-cell"><div class="metric-label">Sim rate</div><div id="simulation-rate" class="metric-value">—</div></div>
      </div>
    </aside>

    <footer class="command-footer flex items-center px-2 text-[8px] tracking-[0.07em] text-[#626262]">
      <span class="friendly mr-5">□ NATO / FRIENDLY</span>
      <span class="hostile mr-5">△ WARSAW PACT / HOSTILE</span>
      <span class="mr-5">─ ALIGNED NATIONAL BORDER</span>
      <span class="warning mr-5">◇ OPERATIONAL EFFECT</span>
      <span class="ml-auto">DISPLAY / WIREFRAME // SOURCE / CAMPAIGN JOURNAL // AUTH / HOST SESSION</span>
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
new HemisphericLight("plot-light", new Vector3(0.2, 1, -0.3), scene).intensity = 0.35;

const gridLines: Vector3[][] = [];
for (let longitude = -180; longitude <= 180; longitude += 15) {
  gridLines.push([worldPosition(longitude, -85), worldPosition(longitude, 85)]);
}
for (let latitude = -75; latitude <= 75; latitude += 15) {
  gridLines.push([worldPosition(-180, latitude), worldPosition(180, latitude)]);
}
const plotGrid = MeshBuilder.CreateLineSystem("plot-grid", { lines: gridLines }, scene);
plotGrid.color = new Color3(0.16, 0.16, 0.16);
plotGrid.alpha = 0.34;
const worldTopology = worldLand as unknown as Parameters<typeof topologyMesh>[0];
const countryTopology = worldCountries as unknown as Parameters<typeof topologyMesh>[0];
const worldCoastline = topologyMesh(worldTopology, worldTopology.objects.land);
const coastlineLines = worldCoastline.coordinates.flatMap(coordinates => splitDateLine(coordinates)
  .filter(segment => segment.length > 1)
  .map(segment => segment.map(([longitude, latitude]) => worldPosition(longitude, latitude, 0.035))));
const coastline = MeshBuilder.CreateLineSystem("world-coastline", { lines: coastlineLines }, scene);
coastline.color = new Color3(0.56, 0.56, 0.56);
coastline.alpha = 0.72;
coastline.isPickable = false;
const plotBorder = MeshBuilder.CreateLines("plot-border", {
  points: [
    worldPosition(-180, -85), worldPosition(-180, 85), worldPosition(180, 85),
    worldPosition(180, -85), worldPosition(-180, -85),
  ],
}, scene);
plotBorder.color = new Color3(0.4, 0.4, 0.4);
plotBorder.alpha = 0.65;

let campaign: Campaign | null = null;
let dynamicMeshes: Mesh[] = [];
let alignedBorderMeshes: Mesh[] = [];
let alignedBorderSignature = "";
let pollHandle: number | undefined;
let selectedEntityId: string | null = null;
const expandedNodes = new Set<string>();
let hierarchyInitialized = false;

function authHeaders(): HeadersInit {
  return {};
}

function worldPosition(longitude: number, latitude: number, y = 0): Vector3 {
  return new Vector3(longitude / 3, y, latitude / 3);
}

function splitDateLine(coordinates: number[][]): number[][][] {
  const segments: number[][][] = [[]];
  for (const coordinate of coordinates) {
    const segment = segments.at(-1)!;
    if (segment.length && Math.abs(segment.at(-1)![0] - coordinate[0]) > 180) {
      segments.push([]);
    }
    segments.at(-1)!.push(coordinate);
  }
  return segments;
}

function mapPosition(latitudeMicrodegrees: number, longitudeMicrodegrees: number, y = 0): Vector3 {
  return worldPosition(longitudeMicrodegrees / 1_000_000, latitudeMicrodegrees / 1_000_000, y);
}

const friendlyBorderColor = new Color3(0.21, 0.91, 0.54);
const hostileBorderColor = new Color3(1, 0.25, 0.32);

function lineSystemFromCoordinates(name: string, coordinates: number[][][], y: number, color: Color3, alpha: number): Mesh | null {
  const lines = coordinates.flatMap(line => splitDateLine(line)
    .filter(segment => segment.length > 1)
    .map(segment => segment.map(([longitude, latitude]) => worldPosition(longitude, latitude, y))));
  if (!lines.length) return null;
  const mesh = MeshBuilder.CreateLineSystem(name, { lines }, scene);
  mesh.color = color;
  mesh.alpha = alpha;
  mesh.isPickable = false;
  return mesh;
}

function countryBorderMesh(countries: AlignedCountry[], mergeInterior: boolean) {
  const ids = new Set(countries.flatMap(country => country.isoNumericIds));
  const mergeIds = new Set(countries.filter(country => country.mergeInterior).flatMap(country => country.isoNumericIds));
  if (!ids.size) return { coordinates: [] as number[][][] };
  return topologyMesh(countryTopology, countryTopology.objects.countries, (left, right) => {
    const leftId = String("id" in left ? left.id ?? "" : "");
    const rightId = String("id" in right ? right.id ?? "" : "");
    const leftAligned = ids.has(leftId);
    const rightAligned = ids.has(rightId);
    if (!leftAligned && !rightAligned) return false;
    if (mergeInterior && left !== right && mergeIds.has(leftId) && mergeIds.has(rightId)) return false;
    return leftAligned || rightAligned;
  });
}

function renderAlignedBorders(data: Campaign): void {
  const represented = countriesRepresentedInOrderOfBattle(data);
  const signature = represented.map(country => country.key).sort().join(",");
  if (signature === alignedBorderSignature) return;
  alignedBorderSignature = signature;
  alignedBorderMeshes.forEach(mesh => mesh.dispose(false, true));
  alignedBorderMeshes = [];
  const drawable = represented.filter(country => country.isoNumericIds.length);
  (["friendly", "hostile"] as const).forEach((alignment: Alignment, index) => {
    const countries = drawable.filter(country => country.alignment === alignment);
    const border = countryBorderMesh(countries, alignment === "hostile");
    const mesh = lineSystemFromCoordinates(
      `aligned-borders-${alignment}`,
      border.coordinates,
      0.05 + index * 0.008,
      alignment === "friendly" ? friendlyBorderColor : hostileBorderColor,
      0.32,
    );
    if (mesh) alignedBorderMeshes.push(mesh);
  });
}

function material(name: string, color: Color3, emissive = 0.55): StandardMaterial {
  const value = new StandardMaterial(name, scene);
  value.diffuseColor = new Color3(0.01, 0.01, 0.01);
  value.emissiveColor = color.scale(emissive);
  value.specularColor = Color3.Black();
  value.disableLighting = true;
  value.wireframe = true;
  value.backFaceCulling = false;
  return value;
}

function coalitionPalette(data: Campaign): Map<string, Color3> {
  const coalitions = [...new Set(data.entities.map(item => item.coalitionId))].sort();
  return new Map(coalitions.map(id => [id, isNato(data, id) ? new Color3(0.21, 0.91, 0.54) : new Color3(1, 0.25, 0.32)]));
}

function isNato(data: Campaign, coalitionId: string): boolean {
  return data.entities.some(item => item.coalitionId === coalitionId
    && (/^(usn_|nor_)/i.test(item.catalogUnitId) || /^(USS|USNS|US V Corps|RNoN)/.test(item.name)));
}

function clearDynamicMap(): void {
  dynamicMeshes.forEach(mesh => mesh.dispose(false, true));
  dynamicMeshes = [];
}

function renderMap(data: Campaign): void {
  clearDynamicMap();
  renderAlignedBorders(data);
  const palette = coalitionPalette(data);
  const entityById = new Map(data.entities.map(item => [item.entityId, item]));
  const regionPositions = new Map<string, Vector3>();
  for (const region of data.regions) {
    const points = [
      ...data.entities.filter(item => item.regionId === region.regionId).map(item => mapPosition(item.latitudeMicrodegrees, item.longitudeMicrodegrees)),
      ...data.bases.filter(item => item.regionId === region.regionId).map(item => mapPosition(item.latitudeMicrodegrees, item.longitudeMicrodegrees)),
    ];
    if (points.length) regionPositions.set(region.regionId, points.reduce((a, b) => a.add(b), Vector3.Zero()).scale(1 / points.length));
  }
  for (let pass = 0; pass < 4; pass++) {
    for (const region of data.regions.filter(item => !regionPositions.has(item.regionId))) {
      const neighbors = region.neighborRegionIds.map(id => regionPositions.get(id)).filter((item): item is Vector3 => !!item);
      if (neighbors.length) regionPositions.set(region.regionId, neighbors.reduce((a, b) => a.add(b), Vector3.Zero()).scale(1 / neighbors.length));
    }
  }

  for (const region of data.regions) {
    const start = regionPositions.get(region.regionId);
    if (!start) continue;
    for (const neighbor of region.neighborRegionIds.filter(id => region.regionId < id)) {
      const end = regionPositions.get(neighbor);
      if (!end) continue;
      const line = MeshBuilder.CreateLines(`route-${region.regionId}-${neighbor}`, { points: [start.add(new Vector3(0, 0.025, 0)), end.add(new Vector3(0, 0.025, 0))] }, scene);
      line.color = new Color3(0.56, 0.56, 0.56);
      line.alpha = 0.46;
      dynamicMeshes.push(line);
    }
    const regionNode = MeshBuilder.CreateBox(`region-${region.regionId}`, { size: 0.34 }, scene);
    regionNode.position = start.add(new Vector3(0, 0.12, 0));
    regionNode.material = material(`region-material-${region.regionId}`, new Color3(0.72, 0.72, 0.72), 0.42);
    dynamicMeshes.push(regionNode);
    const stem = MeshBuilder.CreateLines(`region-stem-${region.regionId}`, {
      points: [start, start.add(new Vector3(0, 0.8, 0))],
    }, scene);
    stem.color = new Color3(0.34, 0.34, 0.34);
    stem.alpha = 0.5;
    dynamicMeshes.push(stem);
  }

  for (const base of data.bases) {
    const marker = MeshBuilder.CreateBox(`base-${base.baseId}`, { width: 0.52, height: 0.18, depth: 0.52 }, scene);
    marker.position = mapPosition(base.latitudeMicrodegrees, base.longitudeMicrodegrees, 0.16);
    marker.material = material(`base-material-${base.baseId}`, palette.get(base.coalitionId) ?? Color3.Gray(), 0.34);
    dynamicMeshes.push(marker);
  }

  for (const entity of data.entities) {
    const marker = entity.simulationDomain === "Air"
      ? MeshBuilder.CreatePolyhedron(`entity-${entity.entityId}`, { type: 0, size: 0.46 }, scene)
      : entity.simulationDomain === "LandAbstract"
        ? MeshBuilder.CreateBox(`entity-${entity.entityId}`, { size: 0.64 }, scene)
        : entity.simulationDomain === "Logistics"
          ? MeshBuilder.CreateBox(`entity-${entity.entityId}`, { width: 0.86, height: 0.34, depth: 0.42 }, scene)
          : MeshBuilder.CreateBox(`entity-${entity.entityId}`, { width: 0.72, height: 0.28, depth: 0.34 }, scene);
    marker.position = mapPosition(entity.latitudeMicrodegrees, entity.longitudeMicrodegrees, 0.35 + (entity.damageMilliunits / 1_000_000) * 0.2);
    const markerColor = entity.entityId === selectedEntityId
      ? new Color3(0.96, 0.96, 0.96)
      : palette.get(entity.coalitionId) ?? Color3.Gray();
    marker.material = material(`entity-material-${entity.entityId}`, markerColor, entity.entityId === selectedEntityId ? 0.9 : 0.58);
    marker.visibility = entity.lifecycle === "Destroyed" ? 0.28 : 1;
    marker.actionManager = new ActionManager(scene);
    marker.actionManager.registerAction(new ExecuteCodeAction(ActionManager.OnPickTrigger, () => selectEntity(entity.entityId)));
    dynamicMeshes.push(marker);
  }

  for (const outcome of data.recentOperationalOutcomes.filter(item => item.targetEntityId).slice(-24)) {
    const actor = entityById.get(outcome.actorEntityId);
    const target = entityById.get(outcome.targetEntityId!);
    if (!actor || !target) continue;
    const line = MeshBuilder.CreateLines(`outcome-${outcome.outcomeId}`, {
      points: [mapPosition(actor.latitudeMicrodegrees, actor.longitudeMicrodegrees, 0.42), mapPosition(target.latitudeMicrodegrees, target.longitudeMicrodegrees, 0.42)],
    }, scene);
    line.color = outcome.kind === "Damage" || outcome.kind === "Destruction" ? new Color3(1, 0.32, 0.18) : new Color3(1, 0.75, 0.20);
    line.alpha = 0.75;
    dynamicMeshes.push(line);
  }
}

function escapeHtml(value: string): string {
  return value.replace(/[&<>'"]/g, char => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", "'": "&#39;", '"': "&quot;" })[char]!);
}

function affiliationIcon(friendly: boolean, label: string): string {
  return `<span class="affiliation-glyph ${friendly ? "friendly" : "hostile"}" aria-label="${label}">
    <svg viewBox="0 0 18 18" aria-hidden="true">
      ${friendly
        ? `<rect x="3.5" y="3.5" width="11" height="11" fill="none" stroke="currentColor" stroke-width="1.2" />`
        : `<path d="M9 2.8 15.2 14.7H2.8Z" fill="none" stroke="currentColor" stroke-width="1.2" />`}
    </svg>
  </span>`;
}

function renderOrderOfBattle(data: Campaign): void {
  const coalitionIds = [...new Set(data.entities.map(entity => entity.coalitionId))]
    .sort((left, right) => Number(isNato(data, right)) - Number(isNato(data, left)) || left.localeCompare(right));
  const rows: string[] = [];
  if (!hierarchyInitialized) {
    for (const coalitionId of coalitionIds) {
      expandedNodes.add(`coalition:${coalitionId}`);
      for (const group of data.groups.filter(item => item.coalitionId === coalitionId)) {
        expandedNodes.add(`group:${group.groupId}`);
      }
    }
    hierarchyInitialized = true;
  }

  for (const coalitionId of coalitionIds) {
    const friendly = isNato(data, coalitionId);
    const coalitionKey = `coalition:${coalitionId}`;
    const coalitionOpen = expandedNodes.has(coalitionKey);
    const coalitionEntities = data.entities.filter(entity => entity.coalitionId === coalitionId);
    rows.push(`<button class="tree-branch tree-coalition" data-toggle="${coalitionKey}" role="treeitem" aria-level="1" aria-expanded="${coalitionOpen}">
      <span class="disclosure ${coalitionOpen ? "disclosure-open" : ""}" aria-hidden="true"></span>
      ${affiliationIcon(friendly, friendly ? "NATO" : "Warsaw Pact")}
      <span class="tree-label"><span>${friendly ? "NATO / FRIENDLY" : "WARSAW PACT / HOSTILE"}</span><small>${coalitionEntities.length} UNITS</small></span>
      <span class="tree-count">${coalitionEntities.filter(entity => entity.fidelity === "Tactical").length.toString().padStart(2, "0")}T</span>
    </button>`);
    if (!coalitionOpen) continue;

    const assignedEntityIds = new Set<string>();
    const groups = data.groups
      .filter(group => group.coalitionId === coalitionId)
      .sort((left, right) => left.name.localeCompare(right.name));
    for (const group of groups) {
      const members = group.memberEntityIds
        .map(entityId => data.entities.find(entity => entity.entityId === entityId))
        .filter((entity): entity is Entity => !!entity && entity.coalitionId === coalitionId)
        .sort((left, right) => left.name.localeCompare(right.name));
      members.forEach(entity => assignedEntityIds.add(entity.entityId));
      if (members.length === 1) {
        const entity = members[0];
        rows.push(`<button data-entity="${entity.entityId}" class="tree-unit register-row ${entity.entityId === selectedEntityId ? "register-row-selected" : ""}" role="treeitem" aria-level="2">
          <span class="tree-rail" aria-hidden="true"></span>
          ${affiliationIcon(friendly, friendly ? "Friendly unit" : "Hostile unit")}
          <span class="tree-label"><span>${escapeHtml(entity.name.toUpperCase())}</span><small>${escapeHtml(group.name.toUpperCase())} / ${escapeHtml(entity.simulationDomain.toUpperCase())}</small></span>
          <span class="tree-fidelity">${escapeHtml(entity.fidelity.slice(0, 3).toUpperCase())}</span>
        </button>`);
        continue;
      }
      const groupKey = `group:${group.groupId}`;
      const groupOpen = expandedNodes.has(groupKey);
      rows.push(`<button class="tree-branch tree-group" data-toggle="${groupKey}" role="treeitem" aria-level="2" aria-expanded="${groupOpen}">
        <span class="tree-rail" aria-hidden="true"></span><span class="disclosure ${groupOpen ? "disclosure-open" : ""}" aria-hidden="true"></span>
        <span class="echelon-glyph" aria-hidden="true"><i></i><i></i></span>
        <span class="tree-label"><span>${escapeHtml(group.name.toUpperCase())}</span><small>${group.activeOrderId ? "ORDER ACTIVE" : "FORMATION"} / ${members.length} UNITS</small></span>
        <span class="tree-count">${members.length.toString().padStart(2, "0")}</span>
      </button>`);
      if (!groupOpen) continue;
      for (const entity of members) {
        rows.push(`<button data-entity="${entity.entityId}" class="tree-unit register-row ${entity.entityId === selectedEntityId ? "register-row-selected" : ""}" role="treeitem" aria-level="3">
          <span class="tree-rail tree-rail-unit" aria-hidden="true"></span>
          ${affiliationIcon(friendly, friendly ? "Friendly unit" : "Hostile unit")}
          <span class="tree-label"><span>${escapeHtml(entity.name.toUpperCase())}</span><small>${escapeHtml(entity.simulationDomain.toUpperCase())} / ${escapeHtml(entity.catalogUnitId.toUpperCase())}</small></span>
          <span class="tree-fidelity">${escapeHtml(entity.fidelity.slice(0, 3).toUpperCase())}</span>
        </button>`);
      }
    }

    const unassigned = coalitionEntities
      .filter(entity => !assignedEntityIds.has(entity.entityId))
      .sort((left, right) => left.name.localeCompare(right.name));
    for (const entity of unassigned) {
      rows.push(`<button data-entity="${entity.entityId}" class="tree-unit register-row ${entity.entityId === selectedEntityId ? "register-row-selected" : ""}" role="treeitem" aria-level="2">
        <span class="tree-rail" aria-hidden="true"></span>
        ${affiliationIcon(friendly, friendly ? "Friendly unit" : "Hostile unit")}
        <span class="tree-label"><span>${escapeHtml(entity.name.toUpperCase())}</span><small>UNASSIGNED / ${escapeHtml(entity.simulationDomain.toUpperCase())}</small></span>
        <span class="tree-fidelity">${escapeHtml(entity.fidelity.slice(0, 3).toUpperCase())}</span>
      </button>`);
    }
  }

  byId("unit-list").innerHTML = rows.join("");
  document.querySelectorAll<HTMLElement>("[data-toggle]").forEach(item => item.addEventListener("click", () => {
    const key = item.dataset.toggle!;
    if (expandedNodes.has(key)) expandedNodes.delete(key); else expandedNodes.add(key);
    renderOrderOfBattle(data);
  }));
  document.querySelectorAll<HTMLElement>("[data-entity]").forEach(item =>
    item.addEventListener("click", () => selectEntity(item.dataset.entity!)));
}

function selectEntity(entityId: string): void {
  if (!campaign) return;
  const entity = campaign.entities.find(item => item.entityId === entityId);
  if (!entity) return;
  selectedEntityId = entityId;
  document.querySelectorAll<HTMLElement>("[data-entity]").forEach(item =>
    item.classList.toggle("register-row-selected", item.dataset.entity === entityId));
  window.requestAnimationFrame(() => campaign && renderMap(campaign));
  const group = campaign.groups.find(item => item.memberEntityIds.includes(entityId));
  const island = campaign.scheduler.islands.find(item => item.entityIds.includes(entityId));
  const assignment = [...campaign.assignments]
    .filter(item => item.entityId === entityId)
    .sort((left, right) => right.authorityEpoch - left.authorityEpoch)[0];
  const damage = (entity.damageMilliunits / 10_000).toFixed(1);
  const operationalWeapons = entity.operationalWeapons ?? [];
  const weaponIds = [...new Set(operationalWeapons.map(item => item.ammunitionId))];
  const maximumWeaponRange = operationalWeapons.reduce(
    (maximum, weapon) => Math.max(maximum, weapon.maximumRangeMeters), 0);
  byId("selection").innerHTML = `
    <div class="border-b border-[#3b3b3b] bg-[#e2e2e2] px-2 py-1 text-[10px] text-black">
      <div class="truncate">${escapeHtml(entity.name.toUpperCase())}</div>
      <div class="truncate text-[8px] text-[#444]">${escapeHtml(entity.entityId)}</div>
    </div>
    <dl class="selection-grid">
      <dt>Domain</dt><dd>${escapeHtml(entity.simulationDomain)}</dd>
      <dt>Catalog</dt><dd>${escapeHtml(entity.catalogUnitId)} / ${escapeHtml(entity.catalogLoadoutId)}</dd>
      <dt>Weapons</dt><dd>${weaponIds.length ? escapeHtml(weaponIds.join(" + ")) : "NONE / FAIL CLOSED"}</dd>
      <dt>Envelope</dt><dd>${maximumWeaponRange ? `${(maximumWeaponRange / 1_000).toFixed(1)} KM MAX / CATALOG` : "NO OPERATIONAL FIRE"}</dd>
      <dt>Fidelity</dt><dd>${escapeHtml(entity.fidelity)} / MAX ${escapeHtml(entity.maximumFidelity)}</dd>
      <dt>Desired</dt><dd class="${island && island.desiredFidelity !== entity.fidelity ? "warning" : ""}">${escapeHtml(island?.desiredFidelity ?? entity.fidelity)}${island?.desiredFidelity === "Tactical" && entity.fidelity !== "Tactical" ? " // AWAIT WORKER" : ""}</dd>
      <dt>Damage</dt><dd class="${Number(damage) > 50 ? "hostile" : ""}">${damage}%</dd>
      <dt>Lifecycle</dt><dd>${escapeHtml(entity.lifecycle)}</dd>
      <dt>Authority</dt><dd>${assignment ? `${escapeHtml(assignment.lifecycle)} / E${assignment.authorityEpoch}` : "OPERATIONAL KERNEL"}</dd>
      <dt>Worker</dt><dd>${assignment?.workerClientInstanceId ? escapeHtml(assignment.workerClientInstanceId.slice(0, 8).toUpperCase()) : "UNASSIGNED"}</dd>
      <dt>Batch</dt><dd>${assignment?.batchId ? `${escapeHtml(assignment.batchId)} / ${assignment.batchSize}` : "SINGLE / NONE"}</dd>
      <dt>Task force</dt><dd>${escapeHtml(group?.name ?? "Unassigned")}</dd>
      <dt>Scheduler</dt><dd>${escapeHtml(island?.reason ?? "Not evaluated")}</dd>
      <dt>Coupling</dt><dd>${island?.couplingScoreMilliunits ?? 0} / 1000 ${island?.engaged ? "// ENGAGED" : "// QUIET"} ${island?.unsafeToSplit ? "// FENCED" : ""}</dd>
      <dt>Edges</dt><dd>${escapeHtml(island?.couplingReasons.join(" + ") || "NONE")}</dd>
    </dl>`;
}

function renderPanels(data: Campaign): void {
  byId("campaign-title").textContent = data.name;
  byId("campaign-time").textContent = `T+${data.timeQuanta}`;
  byId("commit-position").textContent = data.commitPosition.toString();
  byId("unit-count").textContent = `${data.entities.length.toString().padStart(2, "0")} UNITS`;
  byId("region-count").textContent = data.regions.length.toString();
  byId("group-count").textContent = data.groups.length.toString();
  byId("tactical-count").textContent = data.entities.filter(item => item.fidelity === "Tactical").length.toString();
  byId("authority-count").textContent = data.assignments.filter(item => item.lifecycle !== "Released").length.toString();
  byId("worker-count").textContent = data.workers.length.toString();
  byId("tempo").textContent = data.scheduler.targetTicksPerWallSecond > 0 ? `${data.scheduler.targetTicksPerWallSecond}/s` : "—";
  byId("simulation-rate").textContent = data.performance.advance.lastWorkUnitsPerSecondMilliunits > 0
    ? `${(data.performance.advance.lastWorkUnitsPerSecondMilliunits / 1_000).toFixed(0)}/s`
    : "—";
  byId("scheduler-status").textContent = `SCHEDULER / ${data.scheduler.health} // LOAD ${data.scheduler.occupiedTacticalEntityCount}/${data.scheduler.tacticalEntityCapacity} // ACTIVE ${data.scheduler.activeTacticalEntityCount} // HELD ${data.scheduler.retainedSuspendedTacticalEntityCount} // RESERVE ${data.scheduler.tacticalReserveCapacity} // FREE ${data.scheduler.availableTacticalEntityCapacity} // ${data.scheduler.loadAction}`;
  const workerActive = data.workers.reduce((total, worker) => total + worker.ownedActiveEntityCount, 0);
  const workerRecovery = data.workers.reduce((total, worker) => total + worker.ownedRecoveryRequiredEntityCount, 0);
  const workerCapacity = data.workers.reduce((total, worker) => total + worker.tacticalEntityCapacity, 0);
  const workerReserve = data.workers.reduce((total, worker) => total + worker.tacticalReserveCapacity, 0);
  const workerOccupied = data.workers.reduce((total, worker) => total + worker.occupiedTacticalEntityCount, 0);
  const workerPromotionHeadroom = data.workers.reduce((total, worker) => total + worker.promotionHeadroom, 0);
  const workerMigrationHeadroom = data.workers.reduce((total, worker) => total + worker.migrationOverlapHeadroom, 0);
  const workerStatus = `WORKERS / ${data.workers.length} // LOAD ${workerOccupied}/${workerCapacity} // ACTIVE ${workerActive} // RESERVE ${workerReserve} // PROMO ${workerPromotionHeadroom} // MIG ${workerMigrationHeadroom} // RECOVERY ${workerRecovery}`;
  byId("worker-status").textContent = workerStatus;
  byId("worker-status").title = data.workers.length
    ? `${workerStatus} // ${data.workers.map(worker => `${worker.clientInstanceId.slice(0, 8).toUpperCase()}:${worker.health}/${worker.eligibility}:${worker.playerTaskforceId || "NO TASKFORCE"}:O${worker.occupiedTacticalEntityCount}/${worker.tacticalEntityCapacity}:R${worker.tacticalReserveCapacity}:P${worker.promotionHeadroom}:M${worker.migrationOverlapHeadroom}`).join(" + ")}`
    : workerStatus;
  const calibrationLabel = data.calibration?.inEnvelope
    ? "IN-ENVELOPE"
    : `DISAGREE/${data.calibration?.disagreements.length ?? 0}`;
  const replicaEntities = data.replicaConvergence?.entities ?? [];
  const replicaDiverged = replicaEntities.filter(item => item.diagnosis === "unsafe-divergence").length;
  const replicaMatched = replicaEntities.filter(item => item.diagnosis === "exact-match").length;
  const replicaLabel = replicaDiverged ? `UNSAFE/${replicaDiverged}` : `MATCH/${replicaMatched}`;
  const performanceStatus = `PERF / REC ${data.performance.recovery.lastDurationMicroseconds}µs // ADV ${data.performance.advance.lastDurationMicroseconds}µs // SCH ${data.performance.schedule.lastDurationMicroseconds}µs // MEM ${(data.performance.workingSetBytes / 1_048_576).toFixed(0)}M // CAL ${calibrationLabel} // REPLICA ${replicaLabel}`;
  byId("performance-status").textContent = performanceStatus;
  byId("performance-status").title = data.calibration?.inEnvelope
    ? performanceStatus
    : `${performanceStatus} // ${(data.calibration?.disagreements ?? []).join(" // ")}`;
  byId("outcome-count").textContent = `${data.recentOperationalOutcomes.length.toString().padStart(2, "0")} EVENTS`;
  renderOrderOfBattle(data);
  byId("outcome-list").innerHTML = [...data.recentOperationalOutcomes].reverse().map(outcome => `
    <article class="register-row grid grid-cols-[54px_76px_1fr] px-2 py-[5px] text-[9px]">
      <time class="register-dim">T+${outcome.occurredAtQuanta}</time>
      <span class="${outcome.kind === "Damage" || outcome.kind === "Destruction" ? "hostile" : outcome.kind === "Detection" ? "text-[#a8a8a8]" : "warning"}">${escapeHtml(outcome.kind.toUpperCase())}</span>
      <div class="min-w-0"><div class="truncate text-[#c9c9c9]">${escapeHtml(outcome.detail.toUpperCase())}</div>${(outcome.munitionsExpendedMilliunits || outcome.damageInflictedMilliunits) ? `<div class="register-dim truncate text-[8px]">${outcome.munitionsExpendedMilliunits ? `EXP/${outcome.munitionsExpendedMilliunits}` : ""} ${outcome.damageInflictedMilliunits ? `DMG/${outcome.damageInflictedMilliunits}` : ""}</div>` : ""}</div>
    </article>`).join("") || `<div class="register-dim px-2 py-3 text-[9px] uppercase">No events in current advance window</div>`;
}

async function request<T>(path: string, init?: RequestInit): Promise<T> {
  const response = await fetch(path, { ...init, headers: { ...authHeaders(), ...(init?.headers ?? {}) } });
  if (!response.ok) throw new Error(`${response.status} ${await response.text()}`);
  return response.json() as Promise<T>;
}

async function refresh(): Promise<void> {
  const id = byId<HTMLInputElement>("campaign-id").value.trim();
  if (!/^[0-9a-f]{32}$/i.test(id)) throw new Error("Campaign ID must be 32 hexadecimal characters.");
  const [health, next] = await Promise.all([request<{ status: string }>("/health"), request<Campaign>(`/campaigns/${id}`)]);
  campaign = next;
  renderMap(next);
  renderPanels(next);
  byId("health-dot").className = "status-mark friendly";
  byId("health-label").textContent = health.status === "ok" ? "Service healthy" : health.status.toUpperCase();
  byId("connection-message").textContent = `AUTHENTICATED // ${next.canonicalHash.slice(0, 16).toUpperCase()}…`;
}

async function connect(): Promise<void> {
  window.clearInterval(pollHandle);
  try {
    await refresh();
    const id = byId<HTMLInputElement>("campaign-id").value.trim();
    byId<HTMLSelectElement>("campaign-picker").value = id;
    window.history.replaceState({}, "", `${window.location.pathname}?campaign=${id}`);
    pollHandle = window.setInterval(() => refresh().catch(showError), 2_000);
  } catch (error) { showError(error); }
}

async function loadCampaigns(preferredCampaignId = ""): Promise<CampaignSummary[]> {
  const campaigns = await request<CampaignSummary[]>("/campaigns");
  const picker = byId<HTMLSelectElement>("campaign-picker");
  picker.innerHTML = campaigns.length
    ? campaigns.map(item => `<option value="${item.campaignId}">${escapeHtml(item.name.toUpperCase())} // C${item.commitPosition} // ${item.campaignId.slice(0, 8).toUpperCase()}</option>`).join("")
    : `<option value="">NO CAMPAIGNS — CREATE FULDA GAP</option>`;
  const selectedId = preferredCampaignId || byId<HTMLInputElement>("campaign-id").value.trim() || campaigns[0]?.campaignId || "";
  if (campaigns.some(item => item.campaignId === selectedId)) picker.value = selectedId;
  if (/^[0-9a-f]{32}$/i.test(selectedId)) byId<HTMLInputElement>("campaign-id").value = selectedId;
  return campaigns;
}

async function copyCampaignLink(): Promise<void> {
  const id = byId<HTMLInputElement>("campaign-id").value.trim();
  if (!/^[0-9a-f]{32}$/i.test(id)) {
    showError(new Error("Open a campaign before copying its link."));
    return;
  }
  const link = new URL(window.location.href);
  link.searchParams.set("campaign", id);
  await navigator.clipboard.writeText(link.toString());
  byId("connection-message").textContent = `LINK COPIED // ${id.slice(0, 8).toUpperCase()}`;
}

function showError(error: unknown): void {
  byId("health-dot").className = "status-mark hostile";
  byId("health-label").textContent = "UNAVAILABLE";
  byId("connection-message").textContent = error instanceof Error ? error.message : String(error);
}

async function createFulda(): Promise<void> {
  const id = crypto.randomUUID().replaceAll("-", "");
  byId<HTMLInputElement>("campaign-id").value = id;
  try {
    await request(`/control/campaigns`, {
      method: "POST", headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ campaignId: id, name: "Fulda Gap Gone Hot", seed: 19890911, catalogFingerprint: "fulda-gap-1989-v1" }),
    });
    const seed = await request<{ tacticalDemoEntityId: string }>(`/control/campaigns/${id}/bootstrap/fulda-gap`, { method: "POST" });
    await request(`/control/campaigns/${id}/advance/30`, { method: "POST" });
    await request(`/control/campaigns/${id}/schedule`, {
      method: "POST", headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        observedEntityIds: [seed.tacticalDemoEntityId], controlledEntityIds: [], health: "Healthy",
        eligibility: "Eligible", tacticalEntityCapacity: 4, backlogDepth: 0,
      }),
    });
    await loadCampaigns(id);
    await connect();
  } catch (error) { showError(error); }
}

byId("connect").addEventListener("click", connect);
byId("bootstrap").addEventListener("click", createFulda);
byId("copy-link").addEventListener("click", () => void copyCampaignLink().catch(showError));
byId<HTMLSelectElement>("campaign-picker").addEventListener("change", event => {
  const id = (event.currentTarget as HTMLSelectElement).value;
  if (!id) return;
  byId<HTMLInputElement>("campaign-id").value = id;
  void connect();
});
window.addEventListener("resize", () => engine.resize());
engine.runRenderLoop(() => scene.render());

const linkedCampaignId = new URLSearchParams(window.location.search).get("campaign") ?? "";
void (async () => {
  const requestedCampaignId = /^[0-9a-f]{32}$/i.test(linkedCampaignId) ? linkedCampaignId : "";
  const { campaignId: activeCampaignId } = await request<{ campaignId: string | null }>("/active-campaign");
  const targetCampaignId = requestedCampaignId
    && activeCampaignId
    && requestedCampaignId.toLowerCase() === activeCampaignId.toLowerCase()
    ? activeCampaignId
    : activeCampaignId ?? "";
  await loadCampaigns(targetCampaignId);
  if (!targetCampaignId) return;
  byId<HTMLInputElement>("campaign-id").value = targetCampaignId;
  await connect();
})().catch(showError);
