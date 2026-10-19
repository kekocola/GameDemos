import './style.css';
import { BattleAnimator } from './battle-animations';
import type {
  BattleChoice, BattleDebugEntry, BattleEvent, BattleReplay, BattleState, ConfigSummary, LegalChoiceSet, LegalMove,
  RoomState, ServerEnvelope, SnapshotPayload, SpecialAction
} from './protocol';

const app = document.querySelector<HTMLDivElement>('#app')!;
let socket: WebSocket | null = null;
let clientSequence = 0;
let serverSequence = 0;
let battleId = localStorage.getItem('pokebattle.battleId') ?? '';
let playerToken = localStorage.getItem('pokebattle.playerToken') ?? '';
let currentState: BattleState | null = null;
let legalChoices: LegalChoiceSet[] = [];
let config: ConfigSummary | null = null;
const timeline: BattleEvent[] = [];
const rawMessages: string[] = [];
let debugEntries: BattleDebugEntry[] = [];
let loadedReplay: BattleReplay | null = null;
let replayIndex = 0;
let replayTimer: number | null = null;
let roomState: RoomState | null = null;
let controllingSide: number | null = null;
let inviteCode = '';
let autoBattleRunning = false;
let deferBattleResult = false;
let hasPresentedBattle = false;
const battleAnimator = new BattleAnimator();
let snapshotPresentation: Promise<void> = Promise.resolve();
let snapshotGeneration = 0;

app.innerHTML = `
  <header class="topbar">
    <div class="brand"><span class="brand-mark">PB</span><div><strong>POKEBATTLE</strong><small>BATTLE SYSTEM LAB</small></div></div>
    <div class="connection"><span id="connection-dot" class="dot"></span><span id="connection-label">正在连接</span></div>
    <nav class="utility-nav" aria-label="游戏面板">
      <button id="toggle-setup" class="utility-button active" type="button" aria-controls="setup-panel" aria-expanded="true"><span>◇</span>战斗设置</button>
      <button id="toggle-debug" class="utility-button" type="button" aria-controls="debug-panel" aria-expanded="false"><span>≡</span>战斗记录</button>
    </nav>
    <div class="build-tag">CONFIG <span id="config-hash">—</span></div>
  </header>
  <main class="layout">
    <aside id="setup-panel" class="setup panel is-open">
      <div class="panel-title"><span>01</span><div><strong>战斗参数</strong><small>SCENARIO</small></div><button id="close-setup" class="panel-close" type="button" aria-label="关闭战斗设置">×</button></div>
      <label>战斗入口<select id="battle-mode"><option value="Practice">AI 练习</option><option value="Pvp">实时 PVP</option><option value="Dungeon">PVE 副本</option></select></label>
      <label>规则档案<select id="ruleset"></select></label>
      <div class="split"><label>模式<select id="format"><option>Singles</option><option>Doubles</option></select></label><label>随机种子<input id="seed" type="number" value="20260809" /></label></div>
      <label>玩家队伍<select id="team1"></select></label>
      <div id="standard-options"><label>对手队伍<select id="team2"></select></label></div>
      <div id="dungeon-options" class="hidden"><label>PVE 副本<select id="dungeon"></select></label><div id="dungeon-description" class="scenario-note"></div></div>
      <div id="practice-options" class="toggles">
        <label class="toggle"><input id="player-ai" type="checkbox" /><span></span>玩家 AI</label>
        <label class="toggle"><input id="opponent-ai" type="checkbox" checked /><span></span>对手 AI</label>
      </div>
      <button id="create" class="primary">创建新战斗 <span>→</span></button>
      <div id="room-card" class="room-card hidden"></div>
      <div id="pvp-join" class="join-card hidden"><strong>加入 PVP 房间</strong><input id="join-battle-id" placeholder="战斗 ID" /><input id="join-code" placeholder="8 位加入码" maxlength="8" /><button id="join">加入对战</button></div>
      <div class="setup-actions"><button id="snapshot">刷新快照</button><button id="forfeit">投降</button><button id="replay">导出回放</button></div>
      <p class="legal-note">远程图片仅用于本地规则测试；加载失败时使用抽象占位。</p>
    </aside>

    <section class="battle-column">
      <div class="battle-heading"><div class="battle-heading-title" aria-live="polite"><small id="battle-kicker">LIVE SIMULATION</small><h1 id="battle-title">权威战场</h1></div><div class="battle-heading-actions"><button id="auto-battle" class="auto-battle hidden">自动战斗</button><button id="exit-battle" class="exit-battle hidden">退出战斗</button><div id="choice-timer" class="choice-timer hidden"><small>AUTO</small><strong id="choice-countdown">15s</strong></div><div class="turn-pill">TURN <strong id="turn">—</strong></div></div></div>
      <div id="field" class="field" data-weather="clear">
        <div class="field-grid"></div><div class="arena-ring"></div>
        <div id="effect-layer" class="effect-layer" aria-hidden="true"></div>
        <div id="move-banner" class="move-banner" aria-live="polite"><small>MOVE</small><strong></strong></div>
        <div id="battle-actors" class="battle-actors" aria-label="战场精灵"></div>
        <div id="opponent-side" class="side side-opponent empty-state">创建战斗后显示对手</div>
        <div class="field-center"><span id="weather">CLEAR</span><i></i><span id="terrain">NO TERRAIN</span></div>
        <div id="player-side" class="side side-player empty-state">等待战斗数据</div>
      </div>
      <div id="actions" class="actions panel"><div class="empty-action"><strong>行动控制台</strong><span>连接后从服务器获取合法选择</span></div></div>
    </section>

    <aside id="debug-panel" class="debug panel">
      <div class="panel-title"><span>02</span><div><strong>事件观察器</strong><small>TRACE & STATE</small></div><button id="close-debug" class="panel-close" type="button" aria-label="关闭战斗记录">×</button></div>
      <div class="tabs"><button class="active" data-tab="events">事件</button><button data-tab="state">状态</button><button data-tab="debug">日志</button><button data-tab="raw">协议</button><button data-tab="replay">回放</button></div>
      <div id="events-view" class="tab-view event-list"></div>
      <pre id="state-view" class="tab-view hidden"></pre>
      <div id="debug-view" class="tab-view hidden"><div class="debug-toolbar"><select id="debug-level"><option value="">全部等级</option><option>Debug</option><option>Info</option><option>Warning</option></select><select id="debug-category"><option value="">全部分类</option><option>event</option><option>command</option><option>turn</option></select><button id="debug-refresh">刷新</button></div><div id="debug-entries" class="debug-log"></div></div>
      <pre id="raw-view" class="tab-view hidden"></pre>
      <div id="replay-view" class="tab-view hidden"><div class="empty-log">选择“导出回放”载入当前战斗</div></div>
      <input id="replay-file" type="file" accept="application/json,.json" class="hidden" />
    </aside>
    <button id="drawer-scrim" class="drawer-scrim is-visible" type="button" aria-label="关闭面板"></button>
  </main>
  <footer><span>Deterministic event engine</span><span>Gen I—IX · Singles / Doubles</span><span id="battle-ref">NO ACTIVE BATTLE</span></footer>
`;

const byId = <T extends HTMLElement>(id: string) => document.getElementById(id) as T;
type DrawerName = 'setup' | 'debug';

function setDrawer(name: DrawerName, open: boolean): void {
  const setup = byId('setup-panel');
  const debug = byId('debug-panel');
  if (open) {
    setup.classList.toggle('is-open', name === 'setup');
    debug.classList.toggle('is-open', name === 'debug');
  } else {
    byId(`${name}-panel`).classList.remove('is-open');
  }
  const setupOpen = setup.classList.contains('is-open');
  const debugOpen = debug.classList.contains('is-open');
  byId('toggle-setup').classList.toggle('active', setupOpen);
  byId('toggle-debug').classList.toggle('active', debugOpen);
  byId('toggle-setup').setAttribute('aria-expanded', String(setupOpen));
  byId('toggle-debug').setAttribute('aria-expanded', String(debugOpen));
  byId('drawer-scrim').classList.toggle('is-visible', setupOpen || debugOpen);
}

function closeDrawers(): void {
  setDrawer('setup', false);
  setDrawer('debug', false);
}

const setStatus = (connected: boolean, label: string) => {
  byId('connection-dot').classList.toggle('online', connected);
  byId('connection-label').textContent = label;
};

async function loadConfig(): Promise<void> {
  const response = await fetch('/api/config');
  if (!response.ok) throw new Error('无法加载服务端配置摘要');
  config = await response.json() as ConfigSummary;
  byId('config-hash').textContent = config.hash.slice(0, 8);
  byId<HTMLSelectElement>('ruleset').innerHTML = config.ruleSets.map(x => `<option value="${x.id}" ${x.id === 'gen9-sv' ? 'selected' : ''}>${escapeHtml(x.nameZh)}</option>`).join('');
  const teams = config.teams.map(x => `<option value="${x.id}">${escapeHtml(x.nameZh)}</option>`).join('');
  byId<HTMLSelectElement>('team1').innerHTML = teams;
  byId<HTMLSelectElement>('team2').innerHTML = teams;
  byId<HTMLSelectElement>('team1').value = 'modern-violet';
  byId<HTMLSelectElement>('team2').value = 'modern-scarlet';
  byId<HTMLSelectElement>('dungeon').innerHTML = config.dungeons.map(x => `<option value="${x.id}">${escapeHtml(x.nameZh)} · ${x.waves.length} 波</option>`).join('');
  renderSetupMode();
  if (currentState) renderBattleHeading();
}

function renderSetupMode(): void {
  const mode = byId<HTMLSelectElement>('battle-mode').value;
  const practice = mode === 'Practice';
  const dungeon = mode === 'Dungeon';
  byId('standard-options').classList.toggle('hidden', dungeon);
  byId('practice-options').classList.toggle('hidden', !practice);
  byId('dungeon-options').classList.toggle('hidden', !dungeon);
  byId('pvp-join').classList.toggle('hidden', mode !== 'Pvp');
  byId<HTMLSelectElement>('ruleset').disabled = dungeon;
  byId<HTMLSelectElement>('format').disabled = dungeon;
  byId('create').firstChild!.textContent = dungeon ? '创建测试副本 ' : mode === 'Pvp' ? '创建 PVP 房间 ' : '创建新战斗 ';
  const selected = config?.dungeons.find(x => x.id === byId<HTMLSelectElement>('dungeon').value);
  byId('dungeon-description').innerHTML = selected ? `<strong>${escapeHtml(selected.description)}</strong><span>推荐 Lv.${selected.recommendedLevel} · ${escapeHtml(selected.rewardType)} × ${selected.rewardAmount}</span>` : '';
  if (selected && dungeon) {
    byId<HTMLSelectElement>('ruleset').value = selected.rulesetId;
    byId<HTMLSelectElement>('format').value = selected.format;
    byId<HTMLSelectElement>('team1').value = selected.defaultPlayerTeamId;
  }
}

function connect(): void {
  const scheme = location.protocol === 'https:' ? 'wss' : 'ws';
  socket = new WebSocket(`${scheme}://${location.host}/ws`);
  setStatus(false, '正在连接');
  socket.addEventListener('open', () => {
    setStatus(true, 'WebSocket 已连接');
    if (battleId && playerToken) send('reconnect', { battleId, playerToken, afterServerSequence: serverSequence });
  });
  socket.addEventListener('close', () => {
    setStatus(false, '连接已断开');
    window.setTimeout(connect, 1500);
  });
  socket.addEventListener('message', event => receive(String(event.data)));
}

function send(type: string, payload: unknown): void {
  if (!socket || socket.readyState !== WebSocket.OPEN) return;
  const envelope = { protocolVersion: 1, messageId: crypto.randomUUID(), battleId: battleId || undefined, clientSequence: ++clientSequence, type, payload };
  const raw = JSON.stringify(envelope);
  rawMessages.push(`→ ${raw}`);
  socket.send(raw);
  renderRaw();
}

function receive(raw: string): void {
  rawMessages.push(`← ${raw}`);
  if (rawMessages.length > 100) rawMessages.shift();
  renderRaw();
  const message = JSON.parse(raw) as ServerEnvelope;
  serverSequence = Math.max(serverSequence, message.serverSequence);
  if (message.type === 'battleCreated' || message.type === 'battleJoined' || message.type === 'dungeonCreated') {
    const payload = message.payload as { battleId: string; playerToken: string; side: number; mode: string; joinCode?: string };
    if (battleId !== payload.battleId) resetSnapshotPresentation(payload.battleId);
    persistController(payload.battleId, payload.playerToken, payload.side);
    inviteCode = payload.joinCode ?? '';
    if (inviteCode) {
      byId<HTMLInputElement>('join-battle-id').value = battleId;
      byId<HTMLInputElement>('join-code').value = inviteCode;
    }
    renderRoomCard();
  } else if (message.type === 'battleSnapshot') {
    const payload = message.payload as SnapshotPayload;
    queueSnapshotPresentation(payload);
  } else if (message.type === 'battleEvents') {
    const payload = message.payload as { events: BattleEvent[] };
    battleAnimator.queue(payload.events);
    timeline.push(...payload.events);
    if (timeline.length > 200) timeline.splice(0, timeline.length - 200);
    renderTimeline();
  } else if (message.type === 'aiDecision') {
    addSyntheticEvent('AI_DECISION', message.payload as Record<string, unknown>);
  } else if (message.type === 'choiceTimedOut') {
    addSyntheticEvent('CHOICE_TIMEOUT', message.payload as Record<string, unknown>);
  } else if (message.type === 'debugLog') {
    const payload = message.payload as { entries: BattleDebugEntry[] };
    debugEntries = payload.entries ?? [];
    renderDebugLog();
  } else if (message.type === 'roomState') {
    roomState = message.payload as RoomState;
    renderRoomCard();
    updateChoiceCountdown();
  } else if (message.type === 'error') {
    if (autoBattleRunning) {
      finishAutoBattlePresentation();
      renderActions();
    }
    addSyntheticEvent('ERROR', message.payload as Record<string, unknown>);
  }
}

function resetSnapshotPresentation(nextBattleId = ''): void {
  snapshotGeneration++;
  snapshotPresentation = Promise.resolve();
  battleAnimator.reset(nextBattleId);
}

function queueSnapshotPresentation(payload: SnapshotPayload): void {
  const generation = snapshotGeneration;
  snapshotPresentation = snapshotPresentation.then(async () => {
    if (generation !== snapshotGeneration) return;
    const previousState = currentState?.battleId === payload.state.battleId ? currentState : null;
    const animationEvents = battleAnimator.prepareSnapshot(previousState, payload.state);
    deferBattleResult = payload.state.phase === 'Ended' && animationEvents.length > 0;

    if (previousState && animationEvents.length > 0) {
      legalChoices = [];
      renderActions();
      await battleAnimator.play(animationEvents, payload.state);
      if (generation !== snapshotGeneration) return;
    }

    currentState = payload.state;
    legalChoices = autoBattleRunning ? [] : payload.legalChoices ?? [];
    roomState = payload.room;
    controllingSide = payload.controllingSide ?? null;
    battleId = currentState.battleId;
    deferBattleResult = false;
    if (currentState.phase === 'Ended') finishAutoBattlePresentation();
    renderBattle();
    renderRoomCard();
  }).catch(error => {
    console.warn('Battle snapshot presentation skipped after an unexpected error.', error);
  });
}

function persistController(id: string, token: string, side: number): void {
  battleId = id; playerToken = token; controllingSide = side;
  localStorage.setItem('pokebattle.battleId', battleId);
  localStorage.setItem('pokebattle.playerToken', playerToken);
  byId('battle-ref').textContent = `BATTLE ${battleId.slice(0, 8).toUpperCase()}`;
}

function renderRoomCard(): void {
  const card = byId('room-card');
  if (!battleId && !roomState) { card.classList.add('hidden'); return; }
  card.classList.remove('hidden');
  if (roomState?.mode === 'Dungeon' && roomState.dungeon) {
    const d = roomState.dungeon;
    card.innerHTML = `<small>PVE DUNGEON</small><strong>${escapeHtml(d.nameZh)}</strong><span>WAVE ${d.wave} / ${d.totalWaves}${d.completed ? ' · 已完成' : ''}</span><code>${escapeHtml(d.rewardType)} × ${d.rewardAmount}</code>`;
    return;
  }
  const readiness = roomState?.mode === 'Pvp' ? (roomState.ready ? '双方已就绪' : '等待第二名玩家') : '本地练习';
  card.innerHTML = `<small>${roomState?.mode ?? 'ROOM'} · P${controllingSide == null ? '?' : controllingSide + 1}</small><strong>${readiness}</strong><span>${battleId}</span>${inviteCode ? `<code>加入码 ${inviteCode}</code>` : ''}`;
}

function createBattle(): void {
  closeDrawers();
  resetSnapshotPresentation();
  autoBattleRunning = false;
  deferBattleResult = false;
  byId('actions').classList.remove('auto-observing');
  byId('auto-battle').classList.add('hidden');
  byId('exit-battle').classList.add('hidden');
  timeline.length = 0; renderTimeline();
  const mode = byId<HTMLSelectElement>('battle-mode').value;
  if (mode === 'Dungeon') {
    send('createDungeon', {
      dungeonId: byId<HTMLSelectElement>('dungeon').value,
      playerTeamId: byId<HTMLSelectElement>('team1').value,
      seed: Number(byId<HTMLInputElement>('seed').value) || 1
    });
    return;
  }
  send('createBattle', {
    mode,
    rulesetId: byId<HTMLSelectElement>('ruleset').value,
    format: byId<HTMLSelectElement>('format').value,
    team1Id: byId<HTMLSelectElement>('team1').value,
    team2Id: byId<HTMLSelectElement>('team2').value,
    seed: Number(byId<HTMLInputElement>('seed').value) || 1,
    playerAi: byId<HTMLInputElement>('player-ai').checked,
    opponentAi: mode === 'Pvp' ? false : byId<HTMLInputElement>('opponent-ai').checked
  });
}

function joinBattle(): void {
  closeDrawers();
  send('joinBattle', {
    battleId: byId<HTMLInputElement>('join-battle-id').value.trim(),
    joinCode: byId<HTMLInputElement>('join-code').value.trim()
  });
}

function renderBattle(): void {
  if (!currentState) return;
  if (!hasPresentedBattle) {
    hasPresentedBattle = true;
    closeDrawers();
  }
  byId<HTMLSelectElement>('battle-mode').value = currentState.mode;
  if (currentState.dungeonId) byId<HTMLSelectElement>('dungeon').value = currentState.dungeonId;
  byId<HTMLSelectElement>('ruleset').value = currentState.rulesetId;
  byId<HTMLSelectElement>('format').value = currentState.format;
  byId<HTMLInputElement>('seed').value = String(currentState.seed);
  byId('turn').textContent = String(currentState.turn);
  byId('battle-ref').textContent = `BATTLE ${currentState.battleId.slice(0, 8).toUpperCase()}`;
  byId('weather').textContent = currentState.field.weather ? `${currentState.field.weather.toUpperCase()} · ${currentState.field.weatherTurns}` : 'CLEAR';
  byId('terrain').textContent = currentState.field.terrain ? `${currentState.field.terrain.toUpperCase()} · ${currentState.field.terrainTurns}` : 'NO TERRAIN';
  byId('field').dataset.weather = currentState.field.weather || 'clear';
  const exitButton = byId<HTMLButtonElement>('exit-battle');
  exitButton.classList.toggle('hidden', currentState.phase === 'Ended');
  exitButton.disabled = false;
  exitButton.textContent = '退出战斗';
  const controlledSide = controllingSide == null ? null : currentState.sides[controllingSide];
  const autoButton = byId<HTMLButtonElement>('auto-battle');
  const canAutoBattle = currentState.phase !== 'Ended' && currentState.mode !== 'Pvp' && controlledSide != null && !controlledSide.isAi;
  autoButton.classList.toggle('hidden', !canAutoBattle);
  autoButton.disabled = false;
  autoButton.textContent = '自动战斗';
  renderSetupMode();
  byId<HTMLSelectElement>('team1').value = currentState.team1Id;
  renderBattleHeading();
  renderSide(1, 'opponent-side'); renderSide(0, 'player-side');
  renderBattleActors();
  renderActions();
  byId('state-view').textContent = JSON.stringify(currentState, null, 2);
  updateChoiceCountdown();
}

function renderBattleHeading(): void {
  if (!currentState) return;
  const ruleName = config?.ruleSets.find(rule => rule.id === currentState!.rulesetId)?.nameZh ?? currentState.rulesetId;
  if (currentState.mode === 'Dungeon') {
    const dungeon = config?.dungeons.find(item => item.id === currentState!.dungeonId);
    const dungeonName = roomState?.dungeon?.nameZh ?? dungeon?.nameZh ?? currentState.dungeonId ?? 'PVE 副本';
    const wave = dungeon?.waves.find(item => item.wave === currentState!.dungeonWave);
    const totalWaves = currentState.dungeonTotalWaves || roomState?.dungeon?.totalWaves || dungeon?.waves.length || 1;
    byId('battle-title').textContent = dungeonName;
    byId('battle-title').title = dungeonName;
    byId('battle-kicker').textContent = `${wave?.boss ? 'BOSS ' : ''}PVE DUNGEON · WAVE ${currentState.dungeonWave}/${totalWaves}${wave ? ` · ${wave.nameZh}` : ''}`;
    return;
  }

  const firstTeam = config?.teams.find(team => team.id === currentState!.team1Id)?.nameZh ?? currentState.sides[0]?.name ?? currentState.team1Id;
  const secondTeam = config?.teams.find(team => team.id === currentState!.team2Id)?.nameZh ?? currentState.sides[1]?.name ?? currentState.team2Id;
  const title = `${firstTeam} vs ${secondTeam}`;
  byId('battle-title').textContent = title;
  byId('battle-title').title = title;
  byId('battle-kicker').textContent = `${currentState.mode === 'Pvp' ? 'REALTIME PVP' : 'AI PRACTICE'} · ${ruleName}`;
}

function renderSide(sideIndex: number, elementId: string): void {
  const side = currentState?.sides[sideIndex];
  if (!side) return;
  const active = side.active.map((teamIndex, slot) => renderPokemon(side.team[teamIndex], sideIndex, slot)).join('');
  const roster = side.team.map((pokemon, index) => {
    const active = side.active.includes(index);
    const fainted = pokemon.fainted || pokemon.currentHp <= 0;
    const stateLabel = fainted ? '已倒下' : active ? '当前上场' : '后备';
    return `<span class="team-icon ${active ? 'active' : ''} ${fainted ? 'fainted' : ''}" title="${escapeHtml(pokemon.name)} · ${stateLabel}" aria-label="${escapeHtml(pokemon.name)}，${stateLabel}" ${active ? 'aria-current="true"' : ''}>
      <span class="team-icon-fallback">${escapeHtml(pokemon.name.slice(0, 1))}</span><img src="${escapeHtml(pokemon.spriteUrl)}" alt="" loading="lazy" />
    </span>`;
  }).join('');
  byId(elementId).classList.remove('empty-state');
  byId(elementId).innerHTML = `<div class="side-meta"><span>P${sideIndex + 1} · ${side.isAi ? 'AI' : 'HUMAN'}</span><div class="team-roster" aria-label="P${sideIndex + 1} 队伍">${roster}</div></div><div class="active-row">${active}</div>`;
  byId(elementId).querySelectorAll<HTMLImageElement>('img').forEach(img => img.addEventListener('error', () => { img.classList.add('broken'); }));
}

function renderPokemon(pokemon: import('./protocol').PokemonState | undefined, side: number, slot: number): string {
  if (!pokemon) return '';
  const visibleHp = battleAnimator.hpFor(pokemon);
  const hpPercent = Math.max(0, visibleHp / pokemon.maxHp * 100);
  const hpTone = hpPercent <= 25 ? 'critical' : hpPercent <= 50 ? 'warning' : 'healthy';
  const status = pokemon.status !== 'None' ? `<span class="status">${pokemon.status}</span>` : '';
  const special = pokemon.isTerastallized ? 'TERA' : pokemon.isDynamaxed ? 'MAX' : pokemon.isMega ? 'MEGA' : '';
  return `<article class="pokemon-card ${visibleHp <= 0 ? 'is-fainted' : ''}" data-side="${side}" data-slot="${slot}" data-instance-id="${escapeHtml(pokemon.instanceId)}" data-pokemon-name="${escapeHtml(pokemon.name)}" data-max-hp="${pokemon.maxHp}">
    <div class="sprite-shell"><div class="sprite-fallback">${pokemon.name.slice(0, 1)}</div><img src="${pokemon.spriteUrl}" alt="${escapeHtml(pokemon.name)}" /></div>
    <div class="pokemon-info"><div class="pokemon-summary-line"><div class="pokemon-name"><strong>${escapeHtml(pokemon.name)}</strong><span>Lv.${pokemon.level}</span>${special ? `<b>${special}</b>` : ''}</div>
    <div class="types">${pokemon.currentTypes.map(type => `<span data-type="${type}">${type}</span>`).join('')}${status}${pokemon.abilityId ? `<span class="ability" title="特性">${escapeHtml(pokemon.abilityId)}</span>` : ''}</div></div>
    <div class="hp-line ${hpTone}" role="meter" aria-label="${escapeHtml(pokemon.name)} HP ${Math.round(hpPercent)}%" aria-valuemin="0" aria-valuemax="100" aria-valuenow="${Math.round(hpPercent)}"><div><i style="width:${hpPercent}%"></i></div></div>
    ${renderPokemonSkills(pokemon, side, slot)}</div>
  </article>`;
}

function renderPokemonSkills(pokemon: import('./protocol').PokemonState, side: number, slot: number): string {
  const displayedMoves = pokemon.moves.every(move => move.pp <= 0)
    ? [...pokemon.moves, { moveId: 'struggle', pp: 1, maxPp: 1 }]
    : pokemon.moves;
  const skills = displayedMoves.map(move => {
    const definition = config?.moves.find(item => item.id === move.moveId);
    const available = !pokemon.fainted && move.pp > 0;
    return `<span class="pokemon-skill ${available ? '' : 'unavailable'}" data-skill-side="${side}" data-skill-slot="${slot}" data-move-id="${escapeHtml(move.moveId)}" title="${escapeHtml(definition?.nameZh ?? move.moveId)} · PP ${move.pp}/${move.maxPp}"><b>${escapeHtml(definition?.nameZh ?? move.moveId)}</b><small>${move.pp}/${move.maxPp}</small></span>`;
  }).join('');
  return `<div class="pokemon-skills" style="--skill-count:${displayedMoves.length}" aria-label="${side === 0 ? '我方' : '敌方'}技能列表">${skills}</div>`;
}

function renderBattleActors(): void {
  const stage = byId('battle-actors');
  if (!currentState) { stage.innerHTML = ''; return; }
  stage.innerHTML = currentState.sides.flatMap(side => side.active.map((teamIndex, slot) => {
    const pokemon = side.team[teamIndex];
    if (!pokemon) return '';
    const visibleHp = battleAnimator.hpFor(pokemon);
    const spriteUrl = battleSpriteUrl(pokemon.spriteUrl, side.side);
    const mirrorTowardOpponent = side.side === 0 && !spriteUrl.includes('/back/');
    const special = pokemon.isTerastallized ? 'TERA' : pokemon.isDynamaxed ? 'MAX' : pokemon.isMega ? 'MEGA' : '';
    return `<article class="battle-actor actor-side-${side.side} actor-slot-${slot} ${visibleHp <= 0 ? 'is-fainted' : ''}" data-side="${side.side}" data-slot="${slot}" data-instance-id="${escapeHtml(pokemon.instanceId)}" data-pokemon-name="${escapeHtml(pokemon.name)}">
      <div class="actor-shadow"></div><div class="actor-aura"></div>
      <div class="actor-visual"><div class="actor-fallback"><i></i><span>${escapeHtml(pokemon.name.slice(0, 1))}</span></div><img class="${mirrorTowardOpponent ? 'mirror-toward-opponent' : ''}" src="${escapeHtml(spriteUrl)}" data-fallback-src="${escapeHtml(pokemon.spriteUrl)}" alt="${escapeHtml(pokemon.name)} 战斗精灵" /></div>
      <div class="actor-tag"><strong>${escapeHtml(pokemon.name)}</strong><small>Lv.${pokemon.level}${special ? ` · ${special}` : ''}</small></div>
    </article>`;
  })).join('');
  bindSpriteFallbacks(stage);
}

function battleSpriteUrl(spriteUrl: string, side: number): string {
  if (side === 0 && spriteUrl.includes('/other/showdown/') && !spriteUrl.includes('/other/showdown/back/')) {
    return spriteUrl.replace('/other/showdown/', '/other/showdown/back/');
  }
  return spriteUrl;
}

function bindSpriteFallbacks(container: HTMLElement): void {
  container.querySelectorAll<HTMLImageElement>('img').forEach(img => {
    const actor = img.closest<HTMLElement>('.battle-actor');
    const markLoaded = () => actor?.classList.add('has-sprite');
    img.addEventListener('load', markLoaded);
    img.addEventListener('error', () => {
      const fallback = img.dataset.fallbackSrc;
      if (fallback) {
        delete img.dataset.fallbackSrc;
        if (actor?.dataset.side === '0') img.classList.add('mirror-toward-opponent');
        img.src = fallback;
        return;
      }
      img.classList.add('broken');
      actor?.classList.remove('has-sprite');
      actor?.classList.add('uses-fallback');
    });
    if (img.complete && img.naturalWidth > 0) markLoaded();
  });
}

function renderActions(): void {
  const container = byId('actions');
  if (deferBattleResult && currentState?.phase === 'Ended') return;
  if (autoBattleRunning && currentState?.phase !== 'Ended') {
    container.innerHTML = `<div class="empty-action"><strong>自动战斗演算中</strong><span>服务器正在逐回合结算，战场会按事件顺序播放</span></div>`;
    return;
  }
  if (!currentState || currentState.phase === 'Ended') {
    const completed = currentState?.mode === 'Dungeon' && currentState.dungeonCompleted;
    const result = completed ? '副本通关' : currentState?.winnerSide == null ? '平局'
      : controllingSide == null ? `P${currentState.winnerSide + 1} 获胜`
        : currentState.winnerSide === controllingSide ? '战斗胜利' : '战斗失败';
    container.innerHTML = `<div class="battle-result"><small>${completed ? 'DUNGEON COMPLETE' : 'BATTLE COMPLETE'}</small><strong>${result}</strong></div>`;
    return;
  }
  if (legalChoices.length === 0) {
    container.innerHTML = `<div class="empty-action"><strong>等待其他控制方</strong><span>AI 正在计算，或当前选择已经锁定</span></div>`;
    return;
  }
  const instruction = legalChoices.length === 1 ? '点击选择行动，再确认执行' : '依次为每个位置选择行动';
  container.innerHTML = `<div class="action-head"><div><small>COMMAND PHASE</small><strong>${instruction}</strong></div><span>${legalChoices.length} ACTIVE SLOTS</span></div>
    <div class="choice-grid">${legalChoices.map(renderChoiceSet).join('')}</div><button id="submit-choice" class="primary" disabled>确认并执行 <span>→</span></button>`;
  legalChoices.forEach(set => bindChoiceSet(set));
  byId('submit-choice').addEventListener('click', submitChoices);
}

function renderChoiceSet(set: LegalChoiceSet): string {
  const side = currentState!.sides[set.side]!;
  const pokemon = side.team[side.active[set.slot]!]!;
  const displayedMoves = [
    ...pokemon.moves,
    ...set.moves.filter(move => !pokemon.moves.some(slot => slot.moveId === move.moveId)).map(move => ({ moveId: move.moveId, pp: move.pp, maxPp: move.maxPp }))
  ];
  const moveButtons = displayedMoves.map(slot => {
    const move = set.moves.find(candidate => candidate.moveId === slot.moveId);
    const definition = config?.moves.find(item => item.id === slot.moveId);
    const name = move?.nameZh || definition?.nameZh || slot.moveId;
    return `<button type="button" class="skill-button ${move ? '' : 'unavailable'}" data-skill-side="${set.side}" data-skill-slot="${set.slot}" data-move-id="${escapeHtml(slot.moveId)}" aria-pressed="false" ${move ? '' : 'disabled'}><strong>${escapeHtml(name)}</strong><span>${escapeHtml(slot.moveId)}</span><small>PP ${slot.pp} / ${slot.maxPp}</small></button>`;
  }).join('');
  const switchButtons = set.switches.map(index => {
    const candidate = currentState!.sides[set.side]!.team[index]!;
    return `<button type="button" class="choice-option choice-switch" data-switch-index="${index}" aria-pressed="false"><strong>${escapeHtml(candidate.name)}</strong><small>HP ${candidate.currentHp}/${candidate.maxHp}</small></button>`;
  }).join('');
  return `<div class="choice-card" data-slot="${set.slot}" data-confirmed="false"><span class="slot-label">SLOT ${set.slot + 1}</span><div class="skill-grid" style="--skill-count:${displayedMoves.length}">${moveButtons}</div><div class="choice-tools"><div class="choice-option-group target-group hidden"><span>选择目标</span><div class="choice-option-list choice-targets"></div></div><div class="choice-option-group special-group hidden"><span>战斗强化</span><div class="choice-option-list choice-specials"></div></div><div class="choice-option-group switch-group ${set.switches.length ? '' : 'hidden'}"><span>替换出战</span><div class="choice-option-list">${switchButtons}</div></div></div></div>`;
}

function bindChoiceSet(set: LegalChoiceSet): void {
  const card = document.querySelector<HTMLElement>(`.choice-card[data-slot="${set.slot}"]`)!;
  const moveButtons = [...card.querySelectorAll<HTMLButtonElement>('.skill-button')];
  const switchButtons = [...card.querySelectorAll<HTMLButtonElement>('.choice-switch')];
  const targetGroup = card.querySelector<HTMLElement>('.target-group')!;
  const specialGroup = card.querySelector<HTMLElement>('.special-group')!;
  const targetOptions = card.querySelector<HTMLElement>('.choice-targets')!;
  const specialOptions = card.querySelector<HTMLElement>('.choice-specials')!;
  const updateSubmitState = () => {
    const cards = [...document.querySelectorAll<HTMLElement>('.choice-card')];
    byId<HTMLButtonElement>('submit-choice').disabled = cards.length === 0 || cards.some(item => item.dataset.confirmed !== 'true');
  };
  const selectMove = (moveId: string) => {
    card.dataset.kind = 'Move';
    card.dataset.moveId = moveId;
    card.dataset.confirmed = 'true';
    delete card.dataset.switchIndex;
    switchButtons.forEach(button => {
      button.classList.remove('selected');
      button.setAttribute('aria-pressed', 'false');
    });
    moveButtons.forEach(button => {
      const selected = button.dataset.moveId === moveId;
      button.classList.toggle('selected', selected);
      button.setAttribute('aria-pressed', String(selected));
    });
    const move = set.moves.find(x => x.moveId === moveId)!;
    const initialTarget = move.targets[0];
    card.dataset.targetSide = String(initialTarget?.side ?? set.side);
    card.dataset.targetSlot = String(initialTarget?.slot ?? set.slot);
    targetOptions.innerHTML = move.targets.map((target, index) => `<button type="button" class="choice-option choice-target ${index === 0 ? 'selected' : ''}" data-target-side="${target.side}" data-target-slot="${target.slot}" aria-pressed="${index === 0}">P${target.side + 1} · ${escapeHtml(target.name)}</button>`).join('');
    targetGroup.classList.toggle('hidden', move.targets.length <= 1);
    targetOptions.querySelectorAll<HTMLButtonElement>('.choice-target').forEach(button => button.addEventListener('click', () => {
      card.dataset.targetSide = button.dataset.targetSide;
      card.dataset.targetSlot = button.dataset.targetSlot;
      targetOptions.querySelectorAll<HTMLButtonElement>('.choice-target').forEach(option => {
        const selected = option === button;
        option.classList.toggle('selected', selected);
        option.setAttribute('aria-pressed', String(selected));
      });
    }));

    const initialSpecial = move.specialActions[0] ?? 'None';
    card.dataset.specialAction = initialSpecial;
    specialOptions.innerHTML = move.specialActions.map((action, index) => `<button type="button" class="choice-option choice-special ${index === 0 ? 'selected' : ''}" data-special-action="${action}" aria-pressed="${index === 0}">${escapeHtml(specialName(action))}</button>`).join('');
    specialGroup.classList.toggle('hidden', move.specialActions.length <= 1);
    specialOptions.querySelectorAll<HTMLButtonElement>('.choice-special').forEach(button => button.addEventListener('click', () => {
      card.dataset.specialAction = button.dataset.specialAction;
      specialOptions.querySelectorAll<HTMLButtonElement>('.choice-special').forEach(option => {
        const selected = option === button;
        option.classList.toggle('selected', selected);
        option.setAttribute('aria-pressed', String(selected));
      });
    }));
    updateSubmitState();
  };
  moveButtons.filter(button => !button.disabled).forEach(button => button.addEventListener('click', () => selectMove(button.dataset.moveId!)));
  switchButtons.forEach(button => button.addEventListener('click', () => {
    card.dataset.kind = 'Switch';
    card.dataset.switchIndex = button.dataset.switchIndex;
    card.dataset.confirmed = 'true';
    delete card.dataset.moveId;
    delete card.dataset.targetSide;
    delete card.dataset.targetSlot;
    delete card.dataset.specialAction;
    moveButtons.forEach(moveButton => {
      moveButton.classList.remove('selected');
      moveButton.setAttribute('aria-pressed', 'false');
    });
    switchButtons.forEach(option => {
      const selected = option === button;
      option.classList.toggle('selected', selected);
      option.setAttribute('aria-pressed', String(selected));
    });
    targetGroup.classList.add('hidden');
    specialGroup.classList.add('hidden');
    updateSubmitState();
  }));
}

function submitChoices(): void {
  if (legalChoices.length === 0) return;
  const choices: BattleChoice[] = legalChoices.map(set => {
    const card = document.querySelector<HTMLElement>(`.choice-card[data-slot="${set.slot}"]`)!;
    if (card.dataset.kind === 'Switch') return { slot: set.slot, kind: 'Switch', switchIndex: Number(card.dataset.switchIndex) };
    return { slot: set.slot, kind: 'Move', moveId: card.dataset.moveId, targetSide: Number(card.dataset.targetSide), targetSlot: Number(card.dataset.targetSlot), specialAction: (card.dataset.specialAction ?? 'None') as SpecialAction };
  });
  send('choose', { side: legalChoices[0]?.side ?? 0, choices });
  legalChoices = []; renderActions();
}

function renderTimeline(): void {
  const view = byId('events-view');
  view.innerHTML = timeline.length ? [...timeline].reverse().map(event => `<article><span>${String(event.sequence).padStart(3, '0')}</span><div><strong>${eventName(event.type)}</strong><small>TURN ${event.turn}</small><p>${escapeHtml(eventDescription(event))}</p></div></article>`).join('') : '<div class="empty-log">等待战斗事件</div>';
}

function addSyntheticEvent(type: string, data: Record<string, unknown>): void {
  timeline.push({ sequence: serverSequence, turn: currentState?.turn ?? 0, type, data }); renderTimeline();
}

function updateChoiceCountdown(): void {
  const timer = byId('choice-timer');
  const side = controllingSide == null ? null : currentState?.sides[controllingSide];
  const locked = controllingSide == null ? true : currentState?.choiceLocked?.[controllingSide] ?? true;
  const deadline = roomState?.choiceDeadlineUtc;
  const active = Boolean(deadline && currentState?.phase === 'AwaitingChoices' && roomState?.ready && side && !side.isAi && !locked);
  timer.classList.toggle('hidden', !active);
  if (!active || !deadline) return;
  const remaining = Math.max(0, Date.parse(deadline) - Date.now());
  byId('choice-countdown').textContent = `${Math.ceil(remaining / 1000)}s`;
  timer.classList.toggle('urgent', remaining <= 5000);
}

function renderRaw(): void { byId('raw-view').textContent = rawMessages.slice().reverse().join('\n\n'); }

function requestDebug(): void {
  if (!battleId) return;
  send('requestDebug', {
    after: 0,
    level: byId<HTMLSelectElement>('debug-level').value || undefined,
    category: byId<HTMLSelectElement>('debug-category').value || undefined,
    limit: 500
  });
}

function renderDebugLog(): void {
  const view = byId('debug-entries');
  view.innerHTML = debugEntries.length ? [...debugEntries].reverse().map(entry => `<article data-level="${entry.level}"><span>${String(entry.sequence).padStart(4, '0')}</span><div><strong>${escapeHtml(entry.message)}</strong><small>${escapeHtml(entry.level)} · ${escapeHtml(entry.category)} · T${entry.turn}</small><p>${escapeHtml(JSON.stringify(entry.data))}</p></div></article>`).join('') : '<div class="empty-log">暂无匹配的服务端诊断</div>';
}

async function loadCurrentReplay(): Promise<void> {
  if (!battleId) return;
  const response = await fetch(`/api/battles/${battleId}/replay`);
  if (!response.ok) throw new Error(`回放加载失败：HTTP ${response.status}`);
  loadedReplay = await response.json() as BattleReplay;
  replayIndex = 0;
  activateTab('replay');
}

function renderReplay(): void {
  const view = byId('replay-view');
  if (!loadedReplay) { view.innerHTML = '<div class="empty-log">尚未载入回放</div>'; return; }
  const events = loadedReplay.events;
  replayIndex = Math.max(0, Math.min(replayIndex, events.length));
  const event = replayIndex > 0 ? events[replayIndex - 1] : null;
  const progress = events.length ? Math.round(replayIndex / events.length * 100) : 0;
  view.innerHTML = `<div class="replay-summary"><strong>${escapeHtml(loadedReplay.rulesetId)} · ${loadedReplay.format}</strong><span class="${loadedReplay.verified ? 'verified' : 'invalid'}">${loadedReplay.verified ? '✓ 完整性通过' : '! 校验失败'}</span><small>SEED ${loadedReplay.seed} · ${loadedReplay.commands.length} COMMANDS · ${events.length} EVENTS</small></div>
    <div class="replay-progress"><i style="width:${progress}%"></i></div>
    <div class="replay-controls"><button id="replay-prev" ${replayIndex === 0 ? 'disabled' : ''}>←</button><button id="replay-play">${replayTimer == null ? '播放' : '暂停'}</button><button id="replay-next" ${replayIndex >= events.length ? 'disabled' : ''}>→</button><button id="replay-download">下载 JSON</button><button id="replay-import">导入 JSON</button></div>
    <div class="replay-stage"><small>STEP ${replayIndex} / ${events.length}</small><strong>${event ? escapeHtml(eventName(event.type)) : '初始战场'}</strong><p>${event ? escapeHtml(eventDescription(event)) : `${escapeHtml(loadedReplay.team1Id)} VS ${escapeHtml(loadedReplay.team2Id)}`}</p><pre>${escapeHtml(JSON.stringify(event ?? loadedReplay.initialState, null, 2))}</pre></div>`;
  byId<HTMLButtonElement>('replay-prev').addEventListener('click', () => { replayIndex--; renderReplay(); });
  byId<HTMLButtonElement>('replay-next').addEventListener('click', () => { replayIndex++; renderReplay(); });
  byId('replay-play').addEventListener('click', toggleReplay);
  byId('replay-download').addEventListener('click', downloadReplay);
  byId('replay-import').addEventListener('click', () => byId<HTMLInputElement>('replay-file').click());
}

function toggleReplay(): void {
  if (replayTimer != null) { window.clearInterval(replayTimer); replayTimer = null; renderReplay(); return; }
  if (!loadedReplay || replayIndex >= loadedReplay.events.length) replayIndex = 0;
  replayTimer = window.setInterval(() => {
    if (!loadedReplay || replayIndex >= loadedReplay.events.length) {
      if (replayTimer != null) window.clearInterval(replayTimer);
      replayTimer = null; renderReplay(); return;
    }
    replayIndex++; renderReplay();
  }, 700);
  renderReplay();
}

function downloadReplay(): void {
  if (!loadedReplay) return;
  const url = URL.createObjectURL(new Blob([JSON.stringify(loadedReplay, null, 2)], { type: 'application/json' }));
  const link = document.createElement('a'); link.href = url; link.download = `pokebattle-${loadedReplay.battleId}.replay.json`; link.click();
  URL.revokeObjectURL(url);
}

function activateTab(name: string): void {
  document.querySelectorAll<HTMLButtonElement>('.tabs button').forEach(x => x.classList.toggle('active', x.dataset.tab === name));
  document.querySelectorAll('.tab-view').forEach(x => x.classList.add('hidden'));
  byId(`${name}-view`).classList.remove('hidden');
  if (name === 'debug') requestDebug();
  if (name === 'replay') renderReplay();
}

function eventName(type: string): string {
  return ({ MoveUsed: '使用招式', DamageApplied: '伤害结算', HealApplied: '回复', StatusApplied: '状态变化', AbilityActivated: '特性发动', PokemonSwitched: '替换宝可梦', PokemonFainted: '宝可梦倒下', WeatherChanged: '天气变化', TerrainChanged: '场地变化', TurnStarted: '回合开始', TurnEnded: '回合结束', SpecialActivated: '强化机制', BattleEnded: '战斗结束', DungeonStarted: '副本开始', DungeonWaveStarted: '波次开始', DungeonWaveCompleted: '波次完成', DungeonCompleted: '副本通关', DungeonFailed: '副本失败', AI_DECISION: 'AI 决策', CHOICE_TIMEOUT: '超时自动出招', ERROR: '协议错误' } as Record<string, string>)[type] ?? type;
}

function eventDescription(event: BattleEvent): string {
  const d = event.data;
  if (event.type === 'MoveUsed') return `${d.pokemon ?? ''} 使用 ${d.move ?? d.moveId ?? ''}`;
  if (event.type === 'DamageApplied') return `${d.target ?? ''} -${d.amount ?? 0} HP，剩余 ${d.remainingHp ?? 0}`;
  if (event.type === 'PokemonSwitched') return `${d.from ?? ''} → ${d.to ?? ''}`;
  if (event.type === 'AbilityActivated') return `${d.pokemon ?? ''} 发动 ${d.abilityId ?? ''}（${d.trigger ?? ''}）`;
  if (event.type === 'DungeonWaveStarted') return `第 ${d.wave ?? '?'} / ${d.totalWaves ?? '?'} 波 · ${d.name ?? ''}${d.boss ? ' · BOSS' : ''}`;
  if (event.type === 'DungeonWaveCompleted') return `第 ${d.wave ?? '?'} 波完成`;
  if (event.type === 'DungeonCompleted') return `${d.name ?? '副本'}通关 · ${d.rewardType ?? ''} × ${d.rewardAmount ?? 0}`;
  if (event.type === 'DungeonFailed') return `第 ${d.wave ?? '?'} 波挑战失败`;
  if (event.type === 'CHOICE_TIMEOUT') return `P${Number(d.side ?? 0) + 1} 在第 ${d.turn ?? '?'} 回合由服务器自动选择技能`;
  if (event.type === 'ERROR') return String(d.message ?? '未知错误');
  return Object.entries(d).slice(0, 4).map(([key, value]) => `${key}: ${typeof value === 'object' ? '…' : String(value)}`).join(' · ');
}

function specialName(action: SpecialAction): string { return ({ None: '不使用', Mega: 'Mega 进化', ZMove: 'Z 招式', Dynamax: '极巨化', Terastallize: '太晶化' })[action]; }
function escapeHtml(value: string): string { return value.replace(/[&<>"]/g, char => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' })[char]!); }

function exitBattle(): void {
  if (!currentState || currentState.phase === 'Ended') return;
  const button = byId<HTMLButtonElement>('exit-battle');
  button.disabled = true;
  button.textContent = '正在退出…';
  send('forfeit', {});
}

function startAutoBattle(): void {
  if (!currentState || currentState.phase === 'Ended' || currentState.mode === 'Pvp' || controllingSide == null) return;
  const side = currentState.sides[controllingSide];
  if (!side || side.isAi) return;
  const button = byId<HTMLButtonElement>('auto-battle');
  button.disabled = true;
  button.textContent = '自动战斗中…';
  autoBattleRunning = true;
  legalChoices = [];
  byId('actions').classList.add('auto-observing');
  renderActions();
  send('setAi', { side: controllingSide, enabled: true });
}

function finishAutoBattlePresentation(): void {
  autoBattleRunning = false;
  byId('actions').classList.remove('auto-observing');
}

byId('create').addEventListener('click', createBattle);
byId('join').addEventListener('click', joinBattle);
byId('toggle-setup').addEventListener('click', () => setDrawer('setup', !byId('setup-panel').classList.contains('is-open')));
byId('toggle-debug').addEventListener('click', () => setDrawer('debug', !byId('debug-panel').classList.contains('is-open')));
byId('close-setup').addEventListener('click', () => setDrawer('setup', false));
byId('close-debug').addEventListener('click', () => setDrawer('debug', false));
byId('drawer-scrim').addEventListener('click', closeDrawers);
byId('battle-mode').addEventListener('change', renderSetupMode);
byId('dungeon').addEventListener('change', renderSetupMode);
byId('snapshot').addEventListener('click', () => send('requestSnapshot', {}));
byId('forfeit').addEventListener('click', exitBattle);
byId('exit-battle').addEventListener('click', exitBattle);
byId('auto-battle').addEventListener('click', startAutoBattle);
byId('replay').addEventListener('click', () => loadCurrentReplay().catch(error => addSyntheticEvent('ERROR', { message: String(error) })));
byId('debug-refresh').addEventListener('click', requestDebug);
byId<HTMLInputElement>('replay-file').addEventListener('change', async event => {
  const file = (event.currentTarget as HTMLInputElement).files?.[0];
  if (!file) return;
  try {
    const replay = JSON.parse(await file.text()) as BattleReplay;
    if (replay.replayVersion !== 1 || !Array.isArray(replay.events)) throw new Error('不支持的回放文件');
    loadedReplay = replay; replayIndex = 0; activateTab('replay');
  } catch (error) { addSyntheticEvent('ERROR', { message: String(error) }); }
});
document.querySelectorAll<HTMLButtonElement>('.tabs button').forEach(button => button.addEventListener('click', () => activateTab(button.dataset.tab!)));
window.setInterval(updateChoiceCountdown, 200);

connect();
loadConfig().catch(error => addSyntheticEvent('ERROR', { message: String(error) }));
