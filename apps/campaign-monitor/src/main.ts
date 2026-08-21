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
import "./style.css";

type Region = { regionId: string; name: string; neighborRegionIds: string[] };
type Base = {
  baseId: string; name: string; coalitionId: string; regionId: string;
  latitudeMicrodegrees: number; longitudeMicrodegrees: number; resources: Record<string, number>;
};
type Entity = {
  entityId: string; name: string; catalogUnitId: string; coalitionId: string; fidelity: string;
  simulationDomain: string; maximumFidelity: string; regionId: string;
  latitudeMicrodegrees: number; longitudeMicrodegrees: number; fuelMilliunits: number;
  storesMilliunits: number; damageMilliunits: number; lifecycle: string;
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
};
type InteractionIsland = {
  islandId: string; entityIds: string[]; groupIds: string[]; currentFidelity: string;
  desiredFidelity: string; engaged: boolean; couplingScoreMilliunits: number; reason: string;
};
type Scheduler = {
  health: string; eligibility: string; tacticalEntityCapacity: number; backlogDepth: number;
  fixedStepMicroseconds: number; targetTicksPerWallSecond: number; effectiveAtQuanta: number;
  loadAction: string; islands: InteractionIsland[];
};
type Campaign = {
  campaignId: string; name: string; commitPosition: number; timeQuanta: number; canonicalHash: string;
  regions: Region[]; bases: Base[]; entities: Entity[]; groups: Group[];
  assignments: Assignment[]; recentOperationalOutcomes: Outcome[]; scheduler: Scheduler;
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

    <div class="map-annotation map-annotation-left">PLOT / NORTH ATLANTIC—CENTRAL FRONT // GRID 05° // WIRE VOLUME</div>
    <div class="map-annotation map-annotation-right">DRAG ORBIT // WHEEL RANGE // SELECT TRACK</div>

    <aside class="dock dock-left">
      <div class="window-title flex items-center justify-between px-2"><span>CAMPAIGN LINK</span><span class="text-[#626262]">AUTH/LOCAL</span></div>
      <div class="grid gap-1 p-2">
        <div class="mb-1 flex items-center gap-2 text-[9px]"><span class="status-mark friendly"></span><span>HOST-ISSUED SESSION / HTTPONLY</span></div>
        <label class="metric-label" for="campaign-id">Campaign identifier</label>
        <div class="flex gap-1"><input id="campaign-id" class="command-input min-w-0 flex-1" placeholder="32 hexadecimal characters" /><button id="new-id" class="command-cell" title="Generate campaign ID">NEW</button></div>
        <div class="mt-1 grid grid-cols-2 gap-1"><button id="connect" class="command-cell command-cell-primary">CONNECT</button><button id="bootstrap" class="command-cell">SEED FULDA</button></div>
      </div>
      <div class="section-rule px-2 py-1">
        <div id="connection-message" class="min-h-4 truncate text-[9px] text-[#a8a8a8]">SESSION CREDENTIAL HELD BY HOST</div>
        <div id="scheduler-status" class="truncate text-[8px] uppercase tracking-[0.08em] text-[#626262]">SCHEDULER / UNAVAILABLE</div>
      </div>
      <div class="window-title flex items-center justify-between px-2"><span>FORCE REGISTER</span><span id="unit-count" class="text-[#e2e2e2]">00 TRACKS</span></div>
      <div class="grid grid-cols-[20px_minmax(0,1fr)_34px] border-b border-[#252525] px-2 py-1 text-[8px] text-[#626262]"><span>IFF</span><span>IDENTITY / FORMATION</span><span>FID</span></div>
      <div id="unit-list" class="scrollbar min-h-0 flex-1 overflow-y-auto" role="list" aria-label="Campaign units"></div>
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
        <div class="state-cell"><div class="metric-label">Tempo</div><div id="tempo" class="metric-value">—</div></div>
      </div>
    </aside>

    <footer class="command-footer flex items-center px-2 text-[8px] tracking-[0.07em] text-[#626262]">
      <span class="friendly mr-5">□ NATO / FRIENDLY</span>
      <span class="hostile mr-5">△ WARSAW PACT / HOSTILE</span>
      <span class="warning mr-5">◇ OPERATIONAL EFFECT</span>
      <span class="ml-auto">DISPLAY / WIREFRAME // SOURCE / CAMPAIGN JOURNAL // AUTH / HOST SESSION</span>
    </footer>
  </div>`;

const byId = <T extends HTMLElement>(id: string) => document.querySelector<T>(`#${id}`)!;
const canvas = byId<HTMLCanvasElement>("campaign-map");
const engine = new Engine(canvas, true, { preserveDrawingBuffer: true, stencil: true });
const scene = new Scene(engine);
scene.clearColor = new Color4(0.008, 0.008, 0.008, 1);
const camera = new ArcRotateCamera("camera", -Math.PI / 2, 0.88, 54, new Vector3(-8, 0, 2), scene);
camera.lowerRadiusLimit = 12;
camera.upperRadiusLimit = 100;
camera.attachControl(canvas, true);
new HemisphericLight("plot-light", new Vector3(0.2, 1, -0.3), scene).intensity = 0.35;

const gridLines: Vector3[][] = [];
for (let x = -60; x <= 60; x += 5) gridLines.push([new Vector3(x, 0, -32), new Vector3(x, 0, 32)]);
for (let z = -32; z <= 32; z += 4) gridLines.push([new Vector3(-60, 0, z), new Vector3(60, 0, z)]);
const plotGrid = MeshBuilder.CreateLineSystem("plot-grid", { lines: gridLines }, scene);
plotGrid.color = new Color3(0.16, 0.16, 0.16);
plotGrid.alpha = 0.34;
const plotFrame = MeshBuilder.CreateBox("plot-frame", { width: 120, height: 0.18, depth: 64 }, scene);
plotFrame.position.y = -0.08;
plotFrame.material = material("plot-frame-material", new Color3(0.32, 0.32, 0.32), 0.12);

let campaign: Campaign | null = null;
let dynamicMeshes: Mesh[] = [];
let pollHandle: number | undefined;
let selectedEntityId: string | null = null;

function authHeaders(): HeadersInit {
  return {};
}

function mapPosition(latitudeMicrodegrees: number, longitudeMicrodegrees: number, y = 0): Vector3 {
  const lat = latitudeMicrodegrees / 1_000_000;
  const lon = longitudeMicrodegrees / 1_000_000;
  return new Vector3(lon * 0.42, y, (53 - lat) * 0.72);
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
  byId("selection").innerHTML = `
    <div class="border-b border-[#3b3b3b] bg-[#e2e2e2] px-2 py-1 text-[10px] text-black">
      <div class="truncate">${escapeHtml(entity.name.toUpperCase())}</div>
      <div class="truncate text-[8px] text-[#444]">${escapeHtml(entity.entityId)}</div>
    </div>
    <dl class="selection-grid">
      <dt>Domain</dt><dd>${escapeHtml(entity.simulationDomain)}</dd>
      <dt>Fidelity</dt><dd>${escapeHtml(entity.fidelity)} / MAX ${escapeHtml(entity.maximumFidelity)}</dd>
      <dt>Desired</dt><dd class="${island && island.desiredFidelity !== entity.fidelity ? "warning" : ""}">${escapeHtml(island?.desiredFidelity ?? entity.fidelity)}${island?.desiredFidelity === "Tactical" && entity.fidelity !== "Tactical" ? " // AWAIT WORKER" : ""}</dd>
      <dt>Damage</dt><dd class="${Number(damage) > 50 ? "hostile" : ""}">${damage}%</dd>
      <dt>Lifecycle</dt><dd>${escapeHtml(entity.lifecycle)}</dd>
      <dt>Authority</dt><dd>${assignment ? `${escapeHtml(assignment.lifecycle)} / E${assignment.authorityEpoch}` : "OPERATIONAL KERNEL"}</dd>
      <dt>Batch</dt><dd>${assignment?.batchId ? `${escapeHtml(assignment.batchId)} / ${assignment.batchSize}` : "SINGLE / NONE"}</dd>
      <dt>Task force</dt><dd>${escapeHtml(group?.name ?? "Unassigned")}</dd>
      <dt>Scheduler</dt><dd>${escapeHtml(island?.reason ?? "Not evaluated")}</dd>
      <dt>Coupling</dt><dd>${island?.couplingScoreMilliunits ?? 0} / 1000 ${island?.engaged ? "// ENGAGED" : "// QUIET"}</dd>
    </dl>`;
}

function renderPanels(data: Campaign): void {
  byId("campaign-title").textContent = data.name;
  byId("campaign-time").textContent = `T+${data.timeQuanta}`;
  byId("commit-position").textContent = data.commitPosition.toString();
  byId("unit-count").textContent = `${data.entities.length.toString().padStart(2, "0")} TRACKS`;
  byId("region-count").textContent = data.regions.length.toString();
  byId("group-count").textContent = data.groups.length.toString();
  byId("tactical-count").textContent = data.entities.filter(item => item.fidelity === "Tactical").length.toString();
  byId("authority-count").textContent = data.assignments.filter(item => item.lifecycle !== "Released").length.toString();
  byId("tempo").textContent = data.scheduler.targetTicksPerWallSecond > 0 ? `${data.scheduler.targetTicksPerWallSecond}/s` : "—";
  byId("scheduler-status").textContent = `SCHEDULER / ${data.scheduler.health} // ${data.scheduler.eligibility} // ${data.scheduler.loadAction}`;
  byId("outcome-count").textContent = `${data.recentOperationalOutcomes.length.toString().padStart(2, "0")} EVENTS`;
  const groups = new Map(data.groups.flatMap(group => group.memberEntityIds.map(id => [id, group.name])));
  byId("unit-list").innerHTML = data.entities.map(entity => `
    <button data-entity="${entity.entityId}" class="register-row grid w-full grid-cols-[20px_minmax(0,1fr)_34px] items-center px-2 py-[5px] text-left ${entity.entityId === selectedEntityId ? "register-row-selected" : ""}" role="listitem">
      <span class="affiliation-glyph ${isNato(data, entity.coalitionId) ? "friendly" : "hostile"}">${isNato(data, entity.coalitionId) ? "□" : "△"}</span>
      <span class="min-w-0"><span class="block truncate text-[10px] uppercase">${escapeHtml(entity.name)}</span><span class="register-dim block truncate text-[8px] uppercase">${escapeHtml(groups.get(entity.entityId) ?? entity.simulationDomain)}</span></span>
      <span class="register-dim text-right text-[8px] uppercase">${escapeHtml(entity.fidelity.slice(0, 3))}</span>
    </button>`).join("");
  document.querySelectorAll<HTMLElement>("[data-entity]").forEach(item => item.addEventListener("click", () => selectEntity(item.dataset.entity!)));
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
    pollHandle = window.setInterval(() => refresh().catch(showError), 2_000);
  } catch (error) { showError(error); }
}

function showError(error: unknown): void {
  byId("health-dot").className = "status-mark hostile";
  byId("health-label").textContent = "UNAVAILABLE";
  byId("connection-message").textContent = error instanceof Error ? error.message : String(error);
}

async function createFulda(): Promise<void> {
  let id = byId<HTMLInputElement>("campaign-id").value.trim();
  if (!/^[0-9a-f]{32}$/i.test(id)) {
    id = crypto.randomUUID().replaceAll("-", "");
    byId<HTMLInputElement>("campaign-id").value = id;
  }
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
    await connect();
  } catch (error) { showError(error); }
}

byId("connect").addEventListener("click", connect);
byId("bootstrap").addEventListener("click", createFulda);
byId("new-id").addEventListener("click", () => { byId<HTMLInputElement>("campaign-id").value = crypto.randomUUID().replaceAll("-", ""); });
window.addEventListener("resize", () => engine.resize());
engine.runRenderLoop(() => scene.render());
