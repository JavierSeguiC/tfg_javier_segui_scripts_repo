function out = cross_player_convergence(root, varargin)
%CROSS_PLAYER_CONVERGENCE  Overlay d(t) and the 4 force loops, every player, one axis each.
%
%   out = CROSS_PLAYER_CONVERGENCE(root) produces TWO figures:
%     Figure 1: every player's reflex-difficulty trace d(t), all aiming at
%       the same reflex setpoint (default 10 err/min), overlaid on one axis.
%     Figure 2: the 4 FORCE loops (tau_0..tau_3), same treatment, one
%       subplot per lane, all aiming at the same force-margin setpoint
%       (default 0.05).
%   Both: one median + IQR band per player (consistent colour across BOTH
%   figures), plus a marker at each player's converged (t_stab,
%   steady-state level) point. Same target, different steady-state level =
%   the system finding each player's level.
%
%   Restricted to PURE PI sessions (isPI & ~isRuleBased) by default --
%   "converging to a setpoint" is a PI-controller concept; mixing in
%   Rule-based sessions would confound "where the controller converged"
%   with "which controller ran" (RequirePI=false disables this).
%
%   The reflex figure and the force figure use INDEPENDENT session
%   filters: a session qualifies for Figure 1 if its logged reflex
%   setpoint matches Target (within TargetTol); it qualifies for Figure 2
%   if its logged FORCE setpoint matches TargetForce (within
%   TargetForceTol) -- these are not chained, so a session can appear in
%   one figure and not the other (e.g. a keyboard session with force loops
%   disabled has no force data and is naturally absent from Figure 2, but
%   still contributes to Figure 1). Force-loop setpoints are a single
%   value per session shared across all 4 lanes (PIDifficultyController
%   has one targetForceMargin, not per-lane), so one filter covers all 4
%   force subplots. Recordings from before a setpoint was logged use
%   AssumeDefaultForMissing the same way for both.
%
%   TIME AXIS: raw session time (s), each trace aligned to its own start
%   (t_session - t0) -- no normalization. Each player's band is truncated
%   to THAT PLAYER's shortest session (independently per figure) so every
%   point in their band is backed by their full session count.
%
%   CONVERGENCE MARKER: for each session/loop, compute_loop_metrics is run
%   (same engine as everywhere else in this pipeline); a player's marker is
%   plotted at (median t_stab, median uMeanSS) across their stabilized
%   sessions for that loop -- uMeanSS is exactly the "steady-state level"
%   quantity player_ability_summary.m calls capacity, so the two are
%   directly comparable.
%
%   PLAYER COLOURS are assigned ONCE, from the union of players appearing
%   in either figure's session set, so the same player is the same colour
%   in Figure 1 and every subplot of Figure 2.
%
%   out = CROSS_PLAYER_CONVERGENCE(root, Name, Value, ...) accepts:
%     Target, TargetTol            (10, 0.5)    reflex setpoint (err/min) + tolerance.
%     TargetForce, TargetForceTol  (0.05, 0.01) force setpoint + tolerance.
%     AssumeDefaultForMissing (true) see above.
%     GridDt          (0.25)  s, resampling step for each player's band.
%     ShowSessions    (true)  draw thin individual session traces under
%                            each player's band. Ignored (traces always
%                            drawn) when ShowBand=false.
%     RequirePI       (true)  restrict to pure PI sessions. false =
%                            include every controller; the setpoint
%                            filters still apply independently.
%     ShowBand        (true)  aggregate into median + IQR bands. false =
%                            skip aggregation, plot every session's raw
%                            trace individually, coloured by player --
%                            combine with RequirePI=false for a completely
%                            unfiltered "every session in the folder" view.
%                            The convergence-marker star is still drawn.
%     DwellSec, BandK, Alpha, MaxIter, InitTailFrac  forwarded to
%                            compute_loop_metrics for the convergence markers.
%     Plot            (true)  draw both figures.
%     Quiet           (true)  suppress per-folder missing-file warnings.
%
%   RETURNS out, a struct with:
%     .reflex        struct array, one per player (Figure 1 data): profileId,
%                    nSessions, grid, q1/median/q3, tStabMedian, dLevelMedian,
%                    nStabilized, color.
%     .force         1x4 cell array (lanes 0-3), each a struct array with
%                    the same fields as .reflex, for that lane.
%     .excluded      table: folder, profileId, domain ('reflex'|'force'),
%                    setpoint, reason -- every session dropped from either
%                    figure, and why.
%
%   Requires: load_recording.m, build_session_index.m, build_loop_signals.m,
%   compute_loop_metrics.m on the path. Base MATLAB only otherwise.
%   R2024b baseline.

p = inputParser;
p.addParameter('Target',        10,    @(x)isnumeric(x)&&isscalar(x));
p.addParameter('TargetTol',     0.5,   @(x)isnumeric(x)&&isscalar(x)&&x>=0);
p.addParameter('TargetForce',   0.05,  @(x)isnumeric(x)&&isscalar(x));
p.addParameter('TargetForceTol',0.01,  @(x)isnumeric(x)&&isscalar(x)&&x>=0);
p.addParameter('AssumeDefaultForMissing', true, @(x)islogical(x)||isnumeric(x));
p.addParameter('GridDt',        0.25,  @(x)isnumeric(x)&&isscalar(x)&&x>0);
p.addParameter('ShowSessions',  true,  @(x)islogical(x)||isnumeric(x));
p.addParameter('DwellSec',      20,    @(x)isnumeric(x)&&isscalar(x)&&x>0);
p.addParameter('BandK',         2,     @(x)isnumeric(x)&&isscalar(x)&&x>0);
p.addParameter('Alpha',         0.05,  @(x)isnumeric(x)&&isscalar(x)&&x>0&&x<1);
p.addParameter('MaxIter',       10,    @(x)isnumeric(x)&&isscalar(x)&&x>=1);
p.addParameter('InitTailFrac',  0.34,  @(x)isnumeric(x)&&isscalar(x)&&x>0&&x<1);
p.addParameter('RequirePI',     true,  @(x)islogical(x)||isnumeric(x));
p.addParameter('ShowBand',      true,  @(x)islogical(x)||isnumeric(x));
p.addParameter('Plot',          true,  @(x)islogical(x)||isnumeric(x));
p.addParameter('Quiet',         true,  @(x)islogical(x)||isnumeric(x));
p.parse(varargin{:});
opt = p.Results;
opt.AssumeDefaultForMissing = logical(opt.AssumeDefaultForMissing);
opt.ShowSessions = logical(opt.ShowSessions);
opt.RequirePI = logical(opt.RequirePI);
opt.ShowBand  = logical(opt.ShowBand);
opt.Plot  = logical(opt.Plot);
opt.Quiet = logical(opt.Quiet);

idx = build_session_index(root, 'HeavyStats', false, 'Quiet', opt.Quiet);
if height(idx) == 0
    warning('cross_player_convergence:noSessions', 'No sessions found under %s', root);
    out = emptyOut(); return;
end

if opt.RequirePI
    piMask = idx.isPI & ~idx.isRuleBased;
else
    piMask = true(height(idx), 1);
end

[reflexRows, excReflex] = filterSessions(idx, piMask, idx.rReflex, opt.Target, ...
    opt.TargetTol, opt.AssumeDefaultForMissing, "reflex");
[forceRows, excForce] = filterSessions(idx, piMask, idx.rForce, opt.TargetForce, ...
    opt.TargetForceTol, opt.AssumeDefaultForMissing, "force");
excludedT = [excReflex; excForce];

fprintf('Reflex figure: %d/%d sessions (target %.3g +/- %.2g err/min).\n', ...
    height(reflexRows), height(idx), opt.Target, opt.TargetTol);
fprintf('Force figure:  %d/%d sessions (target %.3g +/- %.2g).\n', ...
    height(forceRows), height(idx), opt.TargetForce, opt.TargetForceTol);

if height(reflexRows) == 0 && height(forceRows) == 0
    warning('cross_player_convergence:noneKept', 'No sessions matched any filter.');
    out = emptyOut(); out.excluded = excludedT; return;
end

% ---- One player->colour map shared by BOTH figures ----------------------
unionPlayers = unique([reflexRows.profileId; forceRows.profileId], 'stable');
palette = distinctColours(numel(unionPlayers));
colorMap = containers.Map(cellstr(unionPlayers), num2cell(palette,2));

buildOptAll = struct('ReflexOnly', false, 'RefReflex', opt.Target, 'RefForce', opt.TargetForce);

[reflexPerPlayer, reflexRaw] = aggregateSignal(reflexRows, 1, buildOptAll, opt, colorMap);

forcePerPlayer = cell(1,4);
forceRaw = cell(1,4);
for lane = 0:3
    [forcePerPlayer{lane+1}, forceRaw{lane+1}] = aggregateSignal(forceRows, lane+2, buildOptAll, opt, colorMap);
end

if opt.Plot
    if ~isempty(reflexPerPlayer)
        drawReflexFigure(reflexPerPlayer, reflexRaw, opt);
    end
    if any(~cellfun(@isempty, forcePerPlayer))
        drawForceFigure(forcePerPlayer, forceRaw, opt);
    end
end

out.reflex   = reflexPerPlayer;
out.force    = forcePerPlayer;
out.excluded = excludedT;
end % ===================== end main =====================================


% ========================================================================
function [rowsOut, excT] = filterSessions(idx, piMask, setpointCol, target, tol, assumeDefault, domainLabel)
% Apply the controller + setpoint filter for one figure's data, and build
% its exclusion table for transparency.
matchesTarget = isfinite(setpointCol) & abs(setpointCol - target) <= tol;
missingAssumed = ~isfinite(setpointCol) & assumeDefault;
targetMask = matchesTarget | missingAssumed;
keepMask = piMask & targetMask;

excRows = cell(0,5);
for i = 1:height(idx)
    if keepMask(i), continue; end
    if ~piMask(i)
        reason = "not pure PI";
    elseif isfinite(setpointCol(i))
        reason = "setpoint mismatch";
    else
        reason = "missing setpoint (AssumeDefaultForMissing=false)";
    end
    excRows(end+1,:) = { idx.folder(i), idx.profileId(i), domainLabel, setpointCol(i), reason }; %#ok<AGROW>
end
excT = cell2table(excRows, 'VariableNames', {'folder','profileId','domain','setpoint','reason'});
rowsOut = idx(keepMask, :);
end


% ========================================================================
function [perPlayer, rawSessionsByPlayer] = aggregateSignal(rows, loopIdx, buildOptAll, opt, colorMap)
% Generic per-player aggregation of ONE loop's control action u(t) --
% loopIdx=1 -> reflex (d); loopIdx=2..5 -> force lanes 0..3 (tau). Shared
% by the reflex figure and every force-lane subplot so "how do we build a
% band + convergence marker for one signal, across a player's sessions" is
% defined exactly once.
perPlayer = struct('profileId',{},'displayName',{},'nSessions',{},'grid',{},'q1',{},'median',{},'q3',{}, ...
                    'tStabMedian',{},'dLevelMedian',{},'nStabilized',{},'color',{});
rawSessionsByPlayer = {};

if height(rows) == 0, return; end
[players, ~, playerGroups] = unique(rows.profileId, 'stable');

for pIdx = 1:numel(players)
    pName = players(pIdx);
    if ismissing(pName) || pName == "", pName = "Unknown"; end
    pRows = rows(playerGroups == pIdx, :);

    % Display name (sessionMeta 'name') for legends — profileId (a GUID)
    % is kept as the grouping/colour key but never shown to a human.
    displayName = pRows.name_(1);
    if ismissing(displayName) || displayName == "", displayName = pName; end

    S = struct('trel',{},'sig',{},'dur',{});
    tStabs = []; levels = [];

    for s = 1:height(pRows)
        folder = char(pRows.folder(s));
        try
            rec = load_recording(folder, 'Streams', {'meta','control'}, 'Quiet', opt.Quiet);
            if isempty(rec.control), continue; end
            [loops, t] = build_loop_signals(rec, buildOptAll);
        catch ME
            warning('cross_player_convergence:skip', 'Skipping %s: %s', folder, ME.message);
            continue;
        end
        if loopIdx > numel(loops), continue; end

        u = double(loops(loopIdx).u);
        ok = isfinite(t) & isfinite(u);
        tt = t(ok); uu = u(ok);
        if numel(tt) < 3, continue; end
        [tt, o] = sort(tt); uu = uu(o);
        [tt, iu] = unique(tt, 'stable'); uu = uu(iu);
        trel = tt - tt(1);
        S(end+1) = struct('trel',trel, 'sig',uu, 'dur',trel(end)); %#ok<AGROW>

        m = compute_loop_metrics(t, loops(loopIdx).y, loops(loopIdx).u, loops(loopIdx).r, ...
            'DwellSec',opt.DwellSec, 'BandK',opt.BandK, 'Alpha',opt.Alpha, ...
            'MaxIter',opt.MaxIter, 'InitTailFrac',opt.InitTailFrac);
        if m.stabilized && isfinite(m.uMeanSS)
            tStabs(end+1) = m.tStab; %#ok<AGROW>
            levels(end+1) = m.uMeanSS; %#ok<AGROW>
        end
    end

    if isempty(S), continue; end

    Ttrunc = min([S.dur]);
    tGrid = (0:opt.GridDt:Ttrunc)';
    M = nan(numel(tGrid), numel(S));
    for i = 1:numel(S)
        M(:,i) = interp1(S(i).trel, S(i).sig, tGrid, 'linear', NaN);
    end
    Q = rowQuantile(M, [0.25 0.5 0.75]);

    key = char(pName);
    if isKey(colorMap, key), col = colorMap(key); else, col = [0.5 0.5 0.5]; end

    perPlayer(end+1) = struct('profileId',pName, 'displayName',displayName, 'nSessions',numel(S), 'grid',tGrid, ...
        'q1',Q(:,1), 'median',Q(:,2), 'q3',Q(:,3), ...
        'tStabMedian', medianOrNaN(tStabs), 'dLevelMedian', medianOrNaN(levels), ...
        'nStabilized', numel(tStabs), 'color', col); %#ok<AGROW>
    rawSessionsByPlayer{end+1} = S; %#ok<AGROW>
end
end


% ========================================================================
function v = medianOrNaN(x)
x = x(~isnan(x));
if isempty(x), v = NaN; else, v = median(x); end
end


% ========================================================================
function Q = rowQuantile(M, ps)
% Per-row quantiles across columns, NaN-ignoring, no toolbox. Type-7.
nR = size(M,1); k = numel(ps);
Q = nan(nR, k);
for i = 1:nR
    v = M(i, :);
    v = v(isfinite(v));
    n = numel(v);
    if n == 0, continue; end
    v = sort(v);
    if n == 1, Q(i, :) = v; continue; end
    pos  = ps*(n-1) + 1;
    lo   = floor(pos); hi = ceil(pos);
    frac = pos - lo;
    Q(i, :) = v(lo).*(1-frac) + v(hi).*frac;
end
end


% ========================================================================
function C = distinctColours(n)
% n visually distinct RGB rows, no toolbox. Fixed qualitative base palette,
% extended by golden-ratio hue spacing if more are needed.
if n < 1, C = zeros(0,3); return; end
base = [ ...
    0.00 0.45 0.74; 0.85 0.33 0.10; 0.47 0.67 0.19; 0.49 0.18 0.56; ...
    0.93 0.69 0.13; 0.30 0.75 0.93; 0.64 0.08 0.18; 0.20 0.20 0.20];
if n <= size(base,1)
    C = base(1:n, :);
    return;
end
C = base;
h = 0.13;
while size(C,1) < n
    h = mod(h + 0.61803398875, 1);
    C(end+1, :) = hsv2rgb([h, 0.65, 0.85]); %#ok<AGROW>
end
C = C(1:n, :);
end


% ========================================================================
function out = emptyOut()
s = struct('profileId',{},'displayName',{},'nSessions',{},'grid',{},'q1',{},'median',{},'q3',{}, ...
            'tStabMedian',{},'dLevelMedian',{},'nStabilized',{},'color',{});
out.reflex = s;
out.force = {s, s, s, s};
out.excluded = table();
end


% ========================================================================
function [legH, legTxt] = drawCrossPlayerAxes(ax, perPlayer, rawSessionsByPlayer, opt, yLabel)
% Draws one player-overlay signal into an already-created axes (shared by
% the standalone reflex figure and each force-lane subplot). Returns
% legend handles/labels; the caller decides where to put a legend.
hold(ax,'on'); grid(ax,'on'); box(ax,'on');
legH = []; legTxt = {};
for k = 1:numel(perPlayer)
    pp = perPlayer(k);
    col = pp.color;
    S = rawSessionsByPlayer{k};

    if opt.ShowBand
        if opt.ShowSessions
            for i = 1:numel(S)
                plot(ax, S(i).trel, S(i).sig, '-', ...
                    'Color',[col 0.20], 'LineWidth',0.5, 'HandleVisibility','off');
            end
        end
        patch(ax, [pp.grid; flipud(pp.grid)], [pp.q1; flipud(pp.q3)], col, ...
              'FaceAlpha',0.15, 'EdgeColor','none', 'HandleVisibility','off');
        hLeg = plot(ax, pp.grid, pp.median, '-', 'Color',col, 'LineWidth',2);
    else
        hLeg = gobjects(0);
        for i = 1:numel(S)
            h = plot(ax, S(i).trel, S(i).sig, '-', 'Color',col, 'LineWidth',1.0);
            if i == 1, hLeg = h; else, set(h,'HandleVisibility','off'); end
        end
        if isempty(hLeg)
            hLeg = plot(ax, nan, nan, '-', 'Color',col, 'LineWidth',1.0);
        end
    end

    if isfinite(pp.tStabMedian) && isfinite(pp.dLevelMedian)
        plot(ax, pp.tStabMedian, pp.dLevelMedian, 'p', 'MarkerSize',13, ...
             'MarkerFaceColor',col, 'MarkerEdgeColor',[0.1 0.1 0.1], 'LineWidth',1, ...
             'HandleVisibility','off');
    end

    legH(end+1) = hLeg; %#ok<AGROW>
    legTxt{end+1} = sprintf('%s (n=%d, %d/%d stab.)', pp.displayName, pp.nSessions, ...
        pp.nStabilized, pp.nSessions); %#ok<AGROW>
end
xlabel(ax, 'session time (s)');
ylabel(ax, yLabel, 'Interpreter','tex');
end


% ========================================================================
function drawReflexFigure(perPlayer, rawSessionsByPlayer, opt)
fig = figure('Color','w', 'Name','Cross-player convergence — reflex', ...
             'Position',[80 100 1000 600]);
ax = axes(fig); %#ok<LAXES>
[legH, legTxt] = drawCrossPlayerAxes(ax, perPlayer, rawSessionsByPlayer, opt, 'd  (difficulty)');
title(ax, 'Reflex-difficulty convergence across players (same setpoint)');
if opt.ShowBand
    subtitle(ax, 'median +/- IQR per player, band truncated to that player''s shortest session   |   \bigstar = median (t_{stab}, steady-state d)');
else
    subtitle(ax, 'every session plotted individually (no aggregation)   |   \bigstar = median (t_{stab}, steady-state d)');
end
legend(ax, legH, legTxt, 'Location','best', 'Box','off');
end


% ========================================================================
function drawForceFigure(forcePerPlayer, forceRaw, opt)
fig = figure('Color','w', 'Name','Cross-player convergence — force loops', ...
             'Position',[120 60 1100 850]);
tl = tiledlayout(fig, 2, 2, 'TileSpacing','compact', 'Padding','compact');
if opt.ShowBand
    subLine = 'median +/- IQR per player   |   \bigstar = median (t_{stab}, steady-state tau)';
else
    subLine = 'every session plotted individually   |   \bigstar = median (t_{stab}, steady-state tau)';
end
title(tl, 'Force-loop convergence across players (same setpoint), per lane', 'FontWeight','bold');
subtitle(tl, subLine);

FINGER_NAMES = {'Index','Middle','Ring','Pinky'};   % canonical, post-remap (see load_recording.m)
for lane = 0:3
    ax = nexttile(tl);
    finger = FINGER_NAMES{lane+1};
    yl = sprintf('\\tau_{%s}  (force — %s)', finger, finger);
    [legH, legTxt] = drawCrossPlayerAxes(ax, forcePerPlayer{lane+1}, forceRaw{lane+1}, opt, yl);
    title(ax, finger);
    if lane == 0 && ~isempty(legH)
        legend(ax, legH, legTxt, 'Location','best', 'Box','off', 'FontSize',7);
    end
end
end
