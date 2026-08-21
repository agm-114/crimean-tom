import { ActionManager } from "@babylonjs/core/Actions/actionManager.js";
import { ExecuteCodeAction } from "@babylonjs/core/Actions/directActions.js";
import { ArcRotateCamera } from "@babylonjs/core/Cameras/arcRotateCamera.js";
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
type Campaign = {
  campaignId: string; name: string; commitPosition: number; timeQuanta: number; canonicalHash: string;
  regions: Region[]; bases: Base[]; entities: Entity[]; groups: Group[];
  assignments: unknown[]; recentOperationalOutcomes: Outcome[];
};

const app = document.querySelector<HTMLElement>("#app")!;
app.innerHTML = `
  <div class="scanline relative h-screen w-screen overflow-hidden bg-[#071011] text-[#dce8df]">
    <canvas id="campaign-map" class="absolute inset-0 h-full w-full outline-none" aria-label="Interactive three-dimensional campaign map"></canvas>
    <header class="panel absolute left-4 right-4 top-4 z-10 flex min-h-16 items-center justify-between rounded-xl px-5 py-3">
      <div class="flex items-center gap-4">
        <div class="grid h-9 w-9 place-items-center rounded-lg border border-emerald-400/30 bg-emerald-400/10 text-xs font-bold tracking-[0.2em] text-emerald-300">CT</div>
        <div><div class="text-[0.65rem] uppercase tracking-[0.22em] text-emerald-300/70">Operational picture</div><h1 id="campaign-title" class="text-lg font-semibold">Campaign monitor</h1></div>
      </div>
      <div class="flex items-center gap-6 text-right">
        <div><div class="metric-label">Campaign time</div><div id="campaign-time" class="metric-value">—</div></div>
        <div><div class="metric-label">Commit</div><div id="commit-position" class="metric-value">—</div></div>
        <div class="flex items-center gap-2"><span id="health-dot" class="status-dot bg-amber-400 text-amber-400"></span><span id="health-label" class="text-sm">Disconnected</span></div>
      </div>
    </header>

    <aside class="panel absolute bottom-4 left-4 top-24 z-10 flex w-[21rem] flex-col rounded-xl p-4">
      <div class="metric-label mb-2">Authenticated service link</div>
      <label class="mb-1 text-xs text-[#91aaa1]" for="service-token">Bearer token (memory only)</label>
      <input id="service-token" type="password" autocomplete="off" class="mb-3 rounded-md border border-[#315047] bg-black/30 px-3 py-2 text-xs outline-none focus:border-emerald-400/60" placeholder="Paste .run/campaign-service/service.token" />
      <label class="mb-1 text-xs text-[#91aaa1]" for="campaign-id">Campaign ID</label>
      <div class="mb-3 flex gap-2"><input id="campaign-id" class="min-w-0 flex-1 rounded-md border border-[#315047] bg-black/30 px-3 py-2 font-mono text-xs outline-none focus:border-emerald-400/60" placeholder="32 hex characters" /><button id="new-id" class="rounded-md border border-[#315047] px-3 text-xs hover:bg-white/5" title="Generate campaign ID">New</button></div>
      <div class="mb-4 grid grid-cols-2 gap-2"><button id="connect" class="rounded-md bg-emerald-400/15 px-3 py-2 text-xs font-semibold text-emerald-200 ring-1 ring-inset ring-emerald-400/35 hover:bg-emerald-400/25">Connect</button><button id="bootstrap" class="rounded-md border border-[#315047] px-3 py-2 text-xs hover:bg-white/5">Create Fulda</button></div>
      <div id="connection-message" class="mb-4 min-h-5 text-xs text-[#91aaa1]">The visual shell contains no campaign data or stored credential.</div>
      <div class="mb-2 flex items-center justify-between"><span class="metric-label">Units</span><span id="unit-count" class="metric-value text-xs">0</span></div>
      <div id="unit-list" class="scrollbar min-h-0 flex-1 space-y-1 overflow-y-auto pr-1" role="list" aria-label="Campaign units"></div>
    </aside>

    <aside class="panel absolute bottom-4 right-4 top-24 z-10 flex w-[23rem] flex-col rounded-xl p-4">
      <div id="selection" class="mb-5"><div class="metric-label mb-2">Selection</div><div class="text-sm text-[#91aaa1]">Select a map symbol or unit row.</div></div>
      <div class="mb-2 flex items-center justify-between"><span class="metric-label">Operational outcomes</span><span id="outcome-count" class="metric-value text-xs">0</span></div>
      <div id="outcome-list" class="scrollbar min-h-0 flex-1 space-y-2 overflow-y-auto pr-1" aria-live="polite"></div>
      <div class="mt-4 grid grid-cols-3 gap-2 border-t border-[#315047]/50 pt-4 text-center">
        <div><div class="metric-label">Regions</div><div id="region-count" class="metric-value">0</div></div>
        <div><div class="metric-label">Groups</div><div id="group-count" class="metric-value">0</div></div>
        <div><div class="metric-label">Tactical</div><div id="tactical-count" class="metric-value">0</div></div>
      </div>
    </aside>

    <div class="panel absolute bottom-4 left-[23rem] z-10 flex items-center gap-5 rounded-lg px-4 py-2 text-[0.68rem] text-[#91aaa1]">
      <span><i class="mr-1 inline-block h-2 w-2 rounded-full bg-sky-400"></i>NATO</span>
      <span><i class="mr-1 inline-block h-2 w-2 rounded-full bg-rose-400"></i>Warsaw Pact</span>
      <span><i class="mr-1 inline-block h-2 w-2 rounded-full bg-amber-300"></i>Operational outcome</span>
      <span class="text-[#627c73]">Drag to orbit · wheel to zoom · click to inspect</span>
    </div>
  </div>`;

const byId = <T extends HTMLElement>(id: string) => document.querySelector<T>(`#${id}`)!;
const canvas = byId<HTMLCanvasElement>("campaign-map");
const engine = new Engine(canvas, true, { preserveDrawingBuffer: true, stencil: true });
const scene = new Scene(engine);
scene.clearColor = new Color4(0.025, 0.06, 0.065, 1);
const camera = new ArcRotateCamera("camera", -Math.PI / 2, 0.82, 52, new Vector3(-8, 0, 2), scene);
camera.lowerRadiusLimit = 12;
camera.upperRadiusLimit = 100;
camera.attachControl(canvas, true);
new HemisphericLight("light", new Vector3(0.2, 1, -0.3), scene).intensity = 1.2;

const ground = MeshBuilder.CreateGround("ocean", { width: 120, height: 64 }, scene);
const groundMaterial = new StandardMaterial("ocean-material", scene);
groundMaterial.diffuseColor = new Color3(0.025, 0.10, 0.105);
groundMaterial.specularColor = new Color3(0.03, 0.16, 0.14);
groundMaterial.alpha = 0.82;
ground.material = groundMaterial;

let campaign: Campaign | null = null;
let dynamicMeshes: Mesh[] = [];
let pollHandle: number | undefined;

function authHeaders(): HeadersInit {
  return { Authorization: `Bearer ${byId<HTMLInputElement>("service-token").value.trim()}` };
}

function mapPosition(latitudeMicrodegrees: number, longitudeMicrodegrees: number, y = 0): Vector3 {
  const lat = latitudeMicrodegrees / 1_000_000;
  const lon = longitudeMicrodegrees / 1_000_000;
  return new Vector3(lon * 0.42, y, (53 - lat) * 0.72);
}

function material(name: string, color: Color3, emissive = 0.18): StandardMaterial {
  const value = new StandardMaterial(name, scene);
  value.diffuseColor = color;
  value.emissiveColor = color.scale(emissive);
  value.specularColor = color.scale(0.35);
  return value;
}

function coalitionPalette(data: Campaign): Map<string, Color3> {
  const coalitions = [...new Set(data.entities.map(item => item.coalitionId))].sort();
  return new Map(coalitions.map(id => [id, isNato(data, id) ? new Color3(0.22, 0.66, 0.92) : new Color3(0.96, 0.30, 0.38)]));
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
      line.color = new Color3(0.18, 0.37, 0.34);
      line.alpha = 0.6;
      dynamicMeshes.push(line);
    }
  }

  for (const base of data.bases) {
    const marker = MeshBuilder.CreateCylinder(`base-${base.baseId}`, { diameter: 0.55, height: 0.16, tessellation: 6 }, scene);
    marker.position = mapPosition(base.latitudeMicrodegrees, base.longitudeMicrodegrees, 0.12);
    marker.material = material(`base-material-${base.baseId}`, palette.get(base.coalitionId) ?? Color3.Gray(), 0.1);
    dynamicMeshes.push(marker);
  }

  for (const entity of data.entities) {
    const marker = entity.simulationDomain === "LandAbstract"
      ? MeshBuilder.CreateBox(`entity-${entity.entityId}`, { size: 0.52 }, scene)
      : entity.simulationDomain === "Air"
        ? MeshBuilder.CreatePolyhedron(`entity-${entity.entityId}`, { type: 1, size: 0.38 }, scene)
        : MeshBuilder.CreateSphere(`entity-${entity.entityId}`, { diameter: entity.simulationDomain === "Logistics" ? 0.62 : 0.46, segments: 10 }, scene);
    marker.position = mapPosition(entity.latitudeMicrodegrees, entity.longitudeMicrodegrees, 0.35 + (entity.damageMilliunits / 1_000_000) * 0.2);
    marker.material = material(`entity-material-${entity.entityId}`, palette.get(entity.coalitionId) ?? Color3.Gray());
    marker.visibility = entity.lifecycle === "Destroyed" ? 0.28 : 1;
    marker.actionManager = new ActionManager(scene);
    marker.actionManager.registerAction(new ExecuteCodeAction(ActionManager.OnPickTrigger, () => selectEntity(entity.entityId)));
    dynamicMeshes.push(marker);
  }

  for (const outcome of data.recentOperationalOutcomes.filter(item => item.targetEntityId)) {
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
  const group = campaign.groups.find(item => item.memberEntityIds.includes(entityId));
  const damage = (entity.damageMilliunits / 10_000).toFixed(1);
  byId("selection").innerHTML = `
    <div class="metric-label mb-2">Selection</div>
    <div class="mb-1 text-base font-semibold">${escapeHtml(entity.name)}</div>
    <div class="mb-3 font-mono text-[0.64rem] text-[#627c73]">${escapeHtml(entity.entityId)}</div>
    <div class="grid grid-cols-2 gap-x-3 gap-y-2 text-xs">
      <div><div class="metric-label">Domain</div>${escapeHtml(entity.simulationDomain)}</div>
      <div><div class="metric-label">Fidelity</div>${escapeHtml(entity.fidelity)} / ${escapeHtml(entity.maximumFidelity)}</div>
      <div><div class="metric-label">Damage</div>${damage}%</div>
      <div><div class="metric-label">Lifecycle</div>${escapeHtml(entity.lifecycle)}</div>
      <div class="col-span-2"><div class="metric-label">Task force</div>${escapeHtml(group?.name ?? "Unassigned")}</div>
    </div>`;
}

function renderPanels(data: Campaign): void {
  byId("campaign-title").textContent = data.name;
  byId("campaign-time").textContent = `T+${data.timeQuanta}`;
  byId("commit-position").textContent = data.commitPosition.toString();
  byId("unit-count").textContent = data.entities.length.toString();
  byId("region-count").textContent = data.regions.length.toString();
  byId("group-count").textContent = data.groups.length.toString();
  byId("tactical-count").textContent = data.entities.filter(item => item.fidelity === "Tactical").length.toString();
  byId("outcome-count").textContent = data.recentOperationalOutcomes.length.toString();
  const groups = new Map(data.groups.flatMap(group => group.memberEntityIds.map(id => [id, group.name])));
  byId("unit-list").innerHTML = data.entities.map(entity => `
    <button data-entity="${entity.entityId}" class="flex w-full items-center gap-3 rounded-md border border-transparent px-2 py-2 text-left hover:border-[#315047] hover:bg-white/[0.025]" role="listitem">
      <span class="h-2 w-2 shrink-0 rounded-full ${isNato(data, entity.coalitionId) ? "bg-sky-400" : "bg-rose-400"}"></span>
      <span class="min-w-0 flex-1"><span class="block truncate text-xs">${escapeHtml(entity.name)}</span><span class="block truncate text-[0.62rem] text-[#627c73]">${escapeHtml(groups.get(entity.entityId) ?? entity.simulationDomain)}</span></span>
      <span class="text-[0.6rem] uppercase text-[#78988d]">${escapeHtml(entity.fidelity.slice(0, 3))}</span>
    </button>`).join("");
  document.querySelectorAll<HTMLElement>("[data-entity]").forEach(item => item.addEventListener("click", () => selectEntity(item.dataset.entity!)));
  byId("outcome-list").innerHTML = [...data.recentOperationalOutcomes].reverse().map(outcome => `
    <article class="rounded-md border border-[#315047]/50 bg-black/15 px-3 py-2">
      <div class="mb-1 flex items-center justify-between"><span class="text-xs font-semibold ${outcome.kind === "Damage" || outcome.kind === "Destruction" ? "text-orange-300" : "text-amber-200"}">${escapeHtml(outcome.kind)}</span><time class="font-mono text-[0.6rem] text-[#627c73]">T+${outcome.occurredAtQuanta}</time></div>
      <div class="text-[0.68rem] leading-relaxed text-[#91aaa1]">${escapeHtml(outcome.detail)}</div>
      ${(outcome.munitionsExpendedMilliunits || outcome.damageInflictedMilliunits) ? `<div class="mt-1 font-mono text-[0.6rem] text-[#627c73]">${outcome.munitionsExpendedMilliunits ? `EXP ${outcome.munitionsExpendedMilliunits}` : ""} ${outcome.damageInflictedMilliunits ? `DMG ${outcome.damageInflictedMilliunits}` : ""}</div>` : ""}
    </article>`).join("") || `<div class="text-xs text-[#627c73]">No outcomes in the latest advance window.</div>`;
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
  byId("health-dot").className = "status-dot bg-emerald-400 text-emerald-400";
  byId("health-label").textContent = health.status === "ok" ? "Service healthy" : health.status;
  byId("connection-message").textContent = `Authenticated · ${next.canonicalHash.slice(0, 16)}…`;
}

async function connect(): Promise<void> {
  window.clearInterval(pollHandle);
  try {
    await refresh();
    pollHandle = window.setInterval(() => refresh().catch(showError), 2_000);
  } catch (error) { showError(error); }
}

function showError(error: unknown): void {
  byId("health-dot").className = "status-dot bg-rose-400 text-rose-400";
  byId("health-label").textContent = "Unavailable";
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
    await request(`/control/campaigns/${id}/bootstrap/fulda-gap`, { method: "POST" });
    await request(`/control/campaigns/${id}/advance/30`, { method: "POST" });
    await connect();
  } catch (error) { showError(error); }
}

byId("connect").addEventListener("click", connect);
byId("bootstrap").addEventListener("click", createFulda);
byId("new-id").addEventListener("click", () => { byId<HTMLInputElement>("campaign-id").value = crypto.randomUUID().replaceAll("-", ""); });
window.addEventListener("resize", () => engine.resize());
engine.runRenderLoop(() => scene.render());
