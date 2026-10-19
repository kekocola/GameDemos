import type { BattleEvent, BattleState, PokemonState } from './protocol';

type EffectTheme = 'physical' | 'fire' | 'water' | 'electric' | 'nature' | 'ice' | 'psychic' | 'shadow';
type FloatTone = 'damage' | 'heal' | 'notice';

const wait = (milliseconds: number) => new Promise<void>(resolve => window.setTimeout(resolve, milliseconds));

export class BattleAnimator {
  private battleId = '';
  private pending: BattleEvent[] = [];
  private lastAnimatedSequence = 0;
  private visualHp = new Map<string, number>();
  private chain: Promise<void> = Promise.resolve();
  private generation = 0;
  private readonly reducedMotion = window.matchMedia('(prefers-reduced-motion: reduce)');

  reset(battleId = ''): void {
    this.battleId = battleId;
    this.pending = [];
    this.lastAnimatedSequence = 0;
    this.visualHp.clear();
    this.generation++;
    document.body.classList.remove('battle-animating');
    document.querySelectorAll('.battle-fx, .combat-float').forEach(element => element.remove());
    document.querySelectorAll('.is-casting').forEach(element => element.classList.remove('is-casting'));
    this.hideBanner();
  }

  queue(events: BattleEvent[]): void {
    for (const event of events) {
      if (event.sequence <= this.lastAnimatedSequence || this.pending.some(item => item.sequence === event.sequence)) continue;
      this.pending.push(event);
    }
    this.pending.sort((left, right) => left.sequence - right.sequence);
  }

  prepareSnapshot(previous: BattleState | null, next: BattleState): BattleEvent[] {
    if (this.battleId !== next.battleId) this.reset(next.battleId);

    if (previous?.battleId === next.battleId) {
      this.captureHp(previous, true);
    }

    const ready = this.pending.filter(event => event.sequence <= next.eventSequence);
    this.pending = this.pending.filter(event => event.sequence > next.eventSequence);
    if (ready.length > 0) this.lastAnimatedSequence = Math.max(this.lastAnimatedSequence, ready.at(-1)!.sequence);

    if (next.phase === 'Ended') {
      this.cancelBacklog();
    }

    if (!previous || previous.battleId !== next.battleId || ready.length === 0) {
      this.captureHp(next, true);
    } else {
      this.captureHp(next, false);
    }
    return ready;
  }

  hpFor(pokemon: PokemonState): number {
    return this.visualHp.get(pokemon.instanceId) ?? pokemon.currentHp;
  }

  play(events: BattleEvent[], finalState: BattleState): Promise<void> {
    if (events.length === 0) return Promise.resolve();
    const generation = this.generation;
    this.chain = this.chain.then(async () => {
      if (generation !== this.generation) return;
      if (this.reducedMotion.matches) {
        for (const event of events) {
          if (event.type !== 'MoveUsed') continue;
          const side = this.number(event.data.side);
          const slot = this.number(event.data.slot);
          const pokemonInstanceId = String(event.data.pokemonInstanceId ?? '');
          const moveId = String(event.data.moveId ?? 'move');
          this.setMoveCasting(side, slot, pokemonInstanceId, moveId, true);
          this.showBanner('MOVE', String(event.data.move ?? event.data.moveId ?? '技能发动'), this.themeFor(moveId));
          await wait(280);
          this.setMoveCasting(side, slot, pokemonInstanceId, moveId, false);
        }
        this.syncHp(finalState);
        this.hideBanner();
        return;
      }

      document.body.classList.add('battle-animating');
      for (const event of events) {
        if (generation !== this.generation) return;
        await this.playEvent(event);
      }
      this.syncHp(finalState);
      this.hideBanner();
      document.body.classList.remove('battle-animating');
    }).catch(error => {
      console.warn('Battle animation skipped after an unexpected error.', error);
      this.syncHp(finalState);
      document.body.classList.remove('battle-animating');
    });
    return this.chain;
  }

  private captureHp(state: BattleState, overwrite: boolean): void {
    for (const side of state.sides) {
      for (const pokemon of side.team) {
        if (overwrite || !this.visualHp.has(pokemon.instanceId)) this.visualHp.set(pokemon.instanceId, pokemon.currentHp);
      }
    }
  }

  private cancelBacklog(): void {
    this.generation++;
    this.chain = Promise.resolve();
    document.body.classList.remove('battle-animating');
    document.querySelectorAll('.battle-fx, .combat-float').forEach(element => element.remove());
    document.querySelectorAll('.battle-actor').forEach(element => element.classList.remove('is-acting', 'is-hit', 'is-healing', 'is-empowered', 'is-entering'));
    document.querySelectorAll('.is-casting').forEach(element => element.classList.remove('is-casting'));
    this.hideBanner();
  }

  private async playEvent(event: BattleEvent): Promise<void> {
    const data = event.data;
    if (event.type === 'MoveUsed') {
      const side = this.number(data.side);
      const slot = this.number(data.slot);
      const moveId = String(data.moveId ?? 'move');
      const pokemonInstanceId = String(data.pokemonInstanceId ?? '');
      const actor = this.actorFor(side, slot, pokemonInstanceId);
      const theme = this.themeFor(moveId);
      actor?.classList.add('is-acting');
      this.setMoveCasting(side, slot, pokemonInstanceId, moveId, true);
      this.showBanner('MOVE', String(data.move ?? data.moveId ?? '技能发动'), theme);
      this.spawnMoveEffect(side, theme);
      await wait(780);
      actor?.classList.remove('is-acting');
      this.setMoveCasting(side, slot, pokemonInstanceId, moveId, false);
      return;
    }

    if (event.type === 'DamageApplied' || event.type === 'HealApplied') {
      const healing = event.type === 'HealApplied';
      const targetInstanceId = String(data.targetInstanceId ?? '');
      const targetName = String(data.target ?? '');
      const card = this.cardIdentified(targetInstanceId, targetName);
      const actor = this.actorIdentified(targetInstanceId, targetName);
      const amount = this.number(data.amount);
      const remainingHp = this.number(data.remainingHp);
      if (!healing && remainingHp <= 0) this.hideBanner();
      actor?.classList.add(healing ? 'is-healing' : 'is-hit');
      if (card) {
        this.updateCardHp(card, remainingHp);
      }
      if (actor) this.spawnFloat(actor, `${healing ? '+' : '−'}${amount}`, healing ? 'heal' : 'damage');
      await wait(420);
      actor?.classList.remove('is-healing', 'is-hit');
      return;
    }

    if (event.type === 'MoveMissed' || event.type === 'MoveImmune' || event.type === 'MoveBlocked') {
      const card = this.actorIdentified(String(data.targetInstanceId ?? ''), String(data.target ?? ''));
      const label = event.type === 'MoveMissed' ? 'MISS' : event.type === 'MoveImmune' ? 'IMMUNE' : 'BLOCK';
      if (card) this.spawnFloat(card, label, 'notice');
      await wait(360);
      return;
    }

    if (event.type === 'SpecialActivated' || event.type === 'AbilityActivated' || event.type === 'StatusApplied' || event.type === 'StatStageChanged') {
      const name = String(data.pokemon ?? data.target ?? '');
      const instanceId = String(data.pokemonInstanceId ?? data.targetInstanceId ?? '');
      const card = this.actorIdentified(instanceId, name);
      card?.classList.add('is-empowered');
      const title = event.type === 'AbilityActivated'
        ? String(data.abilityId ?? 'ABILITY')
        : event.type === 'SpecialActivated' ? String(data.action ?? 'POWER UP')
          : event.type === 'StatusApplied' ? String(data.status ?? 'STATUS') : String(data.stat ?? 'STAT CHANGE');
      this.showBanner(event.type === 'AbilityActivated' ? 'ABILITY' : 'BATTLE EFFECT', title, 'psychic');
      await wait(420);
      card?.classList.remove('is-empowered');
      return;
    }

    if (event.type === 'PokemonSwitched' || event.type === 'DungeonWaveStarted') {
      const card = event.type === 'PokemonSwitched'
        ? this.actorIdentified(String(data.toInstanceId ?? ''), String(data.to ?? ''))
        : null;
      (card ?? document.querySelector<HTMLElement>('.battle-actor'))?.classList.add('is-entering');
      if (event.type === 'DungeonWaveStarted') this.showBanner(data.boss ? 'BOSS WAVE' : 'NEXT WAVE', String(data.name ?? `WAVE ${data.wave ?? ''}`), 'shadow');
      await wait(480);
      (card ?? document.querySelector<HTMLElement>('.battle-actor'))?.classList.remove('is-entering');
      return;
    }

    if (event.type === 'PokemonFainted') {
      const card = this.actorIdentified(String(data.instanceId ?? ''), String(data.pokemon ?? ''));
      card?.classList.add('is-fainting');
      await wait(500);
      card?.classList.remove('is-fainting');
      card?.classList.add('is-fainted');
      return;
    }

    if (event.type === 'CriticalHit') {
      const card = this.actorIdentified(String(data.targetInstanceId ?? ''), String(data.target ?? ''));
      if (card) this.spawnFloat(card, 'CRITICAL!', 'notice');
      await wait(240);
    }
  }

  private actorAt(side: number, slot: number): HTMLElement | null {
    return document.querySelector<HTMLElement>(`.battle-actor[data-side="${side}"][data-slot="${slot}"]`);
  }

  private actorFor(side: number, slot: number, pokemonInstanceId: string): HTMLElement | null {
    if (pokemonInstanceId) {
      const actor = document.querySelector<HTMLElement>(`.battle-actor[data-instance-id="${CSS.escape(pokemonInstanceId)}"]`);
      if (actor) return actor;
    }
    return this.actorAt(side, slot);
  }

  private actorNamed(name: string): HTMLElement | null {
    return [...document.querySelectorAll<HTMLElement>('.battle-actor')].find(actor => actor.dataset.pokemonName === name) ?? null;
  }

  private actorIdentified(instanceId: string, fallbackName: string): HTMLElement | null {
    if (instanceId) {
      const actor = document.querySelector<HTMLElement>(`.battle-actor[data-instance-id="${CSS.escape(instanceId)}"]`);
      if (actor) return actor;
      return null;
    }
    return this.actorNamed(fallbackName);
  }

  private cardNamed(name: string): HTMLElement | null {
    return [...document.querySelectorAll<HTMLElement>('.pokemon-card')].find(card => card.dataset.pokemonName === name) ?? null;
  }

  private cardIdentified(instanceId: string, fallbackName: string): HTMLElement | null {
    if (instanceId) {
      const card = document.querySelector<HTMLElement>(`.pokemon-card[data-instance-id="${CSS.escape(instanceId)}"]`);
      if (card) return card;
      return null;
    }
    return this.cardNamed(fallbackName);
  }

  private updateCardHp(card: HTMLElement, hp: number): void {
    const instanceId = card.dataset.instanceId;
    const maxHp = Number(card.dataset.maxHp ?? 1);
    if (instanceId) this.visualHp.set(instanceId, hp);
    const percentage = Math.max(0, Math.min(100, hp / maxHp * 100));
    const hpLine = card.querySelector<HTMLElement>('.hp-line');
    const bar = card.querySelector<HTMLElement>('.hp-line i');
    if (bar) bar.style.width = `${percentage}%`;
    if (hpLine) {
      hpLine.classList.toggle('healthy', percentage > 50);
      hpLine.classList.toggle('warning', percentage > 25 && percentage <= 50);
      hpLine.classList.toggle('critical', percentage <= 25);
      hpLine.setAttribute('aria-valuenow', String(Math.round(percentage)));
      hpLine.setAttribute('aria-label', `${card.dataset.pokemonName ?? 'Pokemon'} HP ${Math.round(percentage)}%`);
    }
    if (hp <= 0) card.classList.add('is-fainted');
  }

  private syncHp(state: BattleState): void {
    this.captureHp(state, true);
    for (const side of state.sides) {
      for (const pokemon of side.team) {
        const card = document.querySelector<HTMLElement>(`.pokemon-card[data-instance-id="${CSS.escape(pokemon.instanceId)}"]`);
        if (!card) continue;
        this.updateCardHp(card, pokemon.currentHp);
        card.classList.toggle('is-fainted', pokemon.fainted);
        const actor = document.querySelector<HTMLElement>(`.battle-actor[data-instance-id="${CSS.escape(pokemon.instanceId)}"]`);
        actor?.classList.toggle('is-fainted', pokemon.fainted);
      }
    }
  }

  private spawnMoveEffect(side: number, theme: EffectTheme): void {
    const layer = document.getElementById('effect-layer');
    if (!layer) return;
    const effect = document.createElement('div');
    effect.className = `battle-fx fx-${theme} from-side-${side}`;
    effect.innerHTML = '<i></i><i></i><i></i><i></i><i></i><i></i>';
    layer.append(effect);
    window.setTimeout(() => effect.remove(), 900);
  }

  private setMoveCasting(side: number, slot: number, pokemonInstanceId: string, moveId: string, active: boolean): void {
    const escapedMoveId = CSS.escape(moveId);
    const selectors = [`.skill-button[data-skill-side="${side}"][data-skill-slot="${slot}"][data-move-id="${escapedMoveId}"]`];
    if (pokemonInstanceId) {
      selectors.push(`.pokemon-card[data-instance-id="${CSS.escape(pokemonInstanceId)}"] .pokemon-skill[data-move-id="${escapedMoveId}"]`);
    } else {
      selectors.push(`.pokemon-skill[data-skill-side="${side}"][data-skill-slot="${slot}"][data-move-id="${escapedMoveId}"]`);
    }
    document.querySelectorAll<HTMLElement>(selectors.join(',')).forEach(element => element.classList.toggle('is-casting', active));
  }

  private spawnFloat(card: HTMLElement, text: string, tone: FloatTone): void {
    const field = document.getElementById('field');
    const layer = document.getElementById('effect-layer');
    if (!field || !layer) return;
    const fieldRect = field.getBoundingClientRect();
    const cardRect = card.getBoundingClientRect();
    const label = document.createElement('span');
    label.className = `combat-float ${tone}`;
    label.textContent = text;
    label.style.left = `${cardRect.left - fieldRect.left + cardRect.width / 2}px`;
    label.style.top = `${cardRect.top - fieldRect.top + cardRect.height / 2}px`;
    layer.append(label);
    window.setTimeout(() => label.remove(), 850);
  }

  private showBanner(kicker: string, title: string, theme: EffectTheme): void {
    const banner = document.getElementById('move-banner');
    if (!banner) return;
    banner.dataset.theme = theme;
    banner.querySelector<HTMLElement>('small')!.textContent = kicker;
    banner.querySelector<HTMLElement>('strong')!.textContent = title;
    banner.classList.remove('visible');
    void banner.offsetWidth;
    banner.classList.add('visible');
  }

  private hideBanner(): void {
    document.getElementById('move-banner')?.classList.remove('visible');
  }

  private themeFor(moveId: string): EffectTheme {
    const id = moveId.toLowerCase();
    if (/(fire|flame|burn|heat|overheat)/.test(id)) return 'fire';
    if (/(grass|leaf|seed|vine|drain|spore)/.test(id)) return 'nature';
    if (/(water|surf|hydro|aqua|rain)/.test(id)) return 'water';
    if (/(thunder|electric|volt|spark)/.test(id)) return 'electric';
    if (/(ice|freeze|snow|blizzard)/.test(id)) return 'ice';
    if (/(psychic|fairy|moon|mind|dazzling)/.test(id)) return 'psychic';
    if (/(dark|shadow|ghost|night|hex)/.test(id)) return 'shadow';
    return 'physical';
  }

  private number(value: unknown): number {
    const parsed = Number(value);
    return Number.isFinite(parsed) ? parsed : 0;
  }
}
