function out = compare_controllers_difficulty_trace(root, opts)
%COMPARE_CONTROLLERS_DIFFICULTY_TRACE  Difficulty d(t) over time, PI vs rule-based.
%
%   out = COMPARE_CONTROLLERS_DIFFICULTY_TRACE(root) scans `root` for
%   recordings, and produces ONE FIGURE PER CONTROLLER (PI, rule-based)
%   with players overlaid. It answers, visually, "does the PI controller
%   converge to a level and stay there, where the rule-based one stays
%   jagged / non-settling?".
%
%   TWO MODES:
%
%   DEFAULT (per-player bands) -- the unimpeachable comparison. For each
%   player, all of that player's sessions under a given controller are
%   aggregated into a median line + IQR (25-75%%) band, drawn in that
%   player's colour. Different players converge to DIFFERENT difficulty
%   levels (that is the whole point of the controller), so the bands fan
%   out vertically -- that spread is real between-player ability, not
%   controller jitter. Within a player the traces are commensurable, so
%   each player's own band width IS controller behaviour.
%
%   DETREND ('Detrend',true) -- the population view. Each session is
%   centred on its OWN steady-state difficulty d_ss (median over the final
%   part of that session), i.e. we plot d(t) - d_ss. That removes the
%   per-player level, making every session commensurable, so ALL sessions
%   across ALL players are POOLED into a single median + IQR band per
%   controller. PI -> band collapses toward zero; rule-based -> band stays
%   wide (keeps oscillating around its level).
%
%   TIME AXIS: raw session time (s), each trace aligned to its own start
%   (t_session - t0). The band is truncated at the SHORTEST session in the
%   group, so every point in the band is backed by the full N sessions
%   (noted in the subtitle). Individual session traces are drawn full-length
%   underneath, thin, so the reader sees the actual N and where they end.
%
%   Options (name-value):
%     Detrend      (false)  plot d - d_ss and pool across players (see above)
%     SteadyFrac   (0.33)   final fraction of each session used to estimate
%                           d_ss for detrending
%     GridDt       (0.25)   s, resampling step for the common band grid
%                           (individual traces are drawn at full resolution)
%     ShowSessions (true)   draw the thin individual session traces
%     Quiet        (true)   suppress per-folder missing-file warnings
%
%   Returns `out`, a struct keyed by controller (valid-name-ified), each
%   with the grid and the band statistics actually plotted, for reuse/tests.
%
%   A session is assigned to a controller's figure only if that controller
%   is the ONLY one it used (pure PI, or pure rule-based); mixed-authority
%   sessions are skipped with a note, so the comparison stays clean.
%
%   Requires: load_recording.m, build_session_index.m on the path. Base
%   MATLAB only (no toolboxes). R2024b baseline.

arguments
    root (1,:) char
    opts.Detrend (1,1) logical = false
    opts.SteadyFrac (1,1) double {mustBePositive} = 0.33
    opts.GridDt (1,1) double {mustBePositive} = 0.25
    opts.ShowSessions (1,1) logical = true
    opts.Quiet (1,1) logical = true
end

idx = build_session_index(root, 'HeavyStats', false, 'Quiet', opts.Quiet);
if height(idx) == 0
    error('compare_controllers:noSessions', 'No sessions found under %s', root);
end

% ---- Define the controller groups (pure sessions only) -----------------
groups = struct('label', {'PI', 'Rule-based'}, ...
                'mask',  {idx.isPI & ~idx.isRuleBased, ...
                          idx.isRuleBased & ~idx.isPI});

% Warn once about mixed-authority sessions that will be skipped.
nMixed = nnz(idx.isPI & idx.isRuleBased);
if nMixed > 0
    fprintf(['Note: %d mixed-authority session(s) (both PI and rule-based) ' ...
             'skipped — a difficulty comparison needs pure sessions.\n'], nMixed);
end

% ---- Consistent per-player colours across BOTH figures -----------------
included = groups(1).mask | groups(2).mask;
players  = unique(idx.profileId(included), 'stable');
players(ismissing(players)) = [];
palette  = distinctColours(max(numel(players), 1));
colourOf = containers.Map('KeyType','char','ValueType','any');
for i = 1:numel(players)
    colourOf(char(players(i))) = palette(i, :);
end

out = struct();
for g = 1:numel(groups)
    rows = idx(groups(g).mask, :);
    if height(rows) == 0
        fprintf('No pure %s sessions — skipping that figure.\n', groups(g).label);
        continue;
    end
    res = drawControllerFigure(rows, groups(g).label, colourOf, opts);
    out.(matlab.lang.makeValidName(groups(g).label)) = res;
end
end % ===================== end main =====================================


% ========================================================================
function res = drawControllerFigure(rows, label, colourOf, opts)
% Load every session's d(t), build the common grid, aggregate, and plot.

S = struct('player',{},'playerName',{},'trel',{},'d',{},'dss',{},'dur',{});
for i = 1:height(rows)
    folder = char(rows.folder(i));
    rec = load_recording(folder, 'Streams', {'control'}, 'Quiet', true);
    if isempty(rec.control) || ~ismember('d', rec.control.Properties.VariableNames)
        warning('compare_controllers:noControl', 'No control/d in %s — skipped.', folder);
        continue;
    end
    t = double(rec.control.t_session);
    d = double(rec.control.d);
    ok = isfinite(t) & isfinite(d);
    t = t(ok); d = d(ok);
    if numel(t) < 3, continue; end
    [t, o] = sort(t); d = d(o);
    [t, iu] = unique(t, 'stable'); d = d(iu);
    trel = t - t(1);

    % steady-state d for detrending: median over final SteadyFrac by time.
    tailStart = trel(end) - opts.SteadyFrac * (trel(end) - trel(1));
    dss = median(d(trel >= tailStart), 'omitnan');

    pid = char(rows.profileId(i));
    if isempty(pid) || strcmp(pid, "<missing>"), pid = char(rows.name(i)); end
    dName = char(rows.name_(i));
    if isempty(dName) || strcmp(dName, "<missing>"), dName = pid; end

    S(end+1) = struct('player',pid, 'playerName',dName, 'trel',trel, 'd',d, 'dss',dss, ...
                      'dur',trel(end)); %#ok<AGROW>
end

if isempty(S)
    fprintf('All %s sessions unusable (no control data) — skipping figure.\n', label);
    res = struct('label',label,'grid',[],'nSessions',0);
    return;
end

Ttrunc = min([S.dur]);
tGrid = (0:opts.GridDt:Ttrunc)';

% signal to plot per session (raw or detrended), plus its grid resample
for i = 1:numel(S)
    S(i).y = S(i).d - opts.Detrend * S(i).dss;      % detrend toggles the subtraction
    S(i).ycol = interp1(S(i).trel, S(i).y, tGrid, 'linear', NaN);
end

% ---- figure ------------------------------------------------------------
fig = figure('Color','w', 'Name', sprintf('Difficulty trace — %s', label), ...
             'Position',[100 100 900 520]);
ax = axes(fig); hold(ax,'on'); grid(ax,'on'); box(ax,'on');

legH = []; legTxt = {};
uPlayers = unique({S.player}, 'stable');

if opts.Detrend
    % -------- pooled band across all players ---------------------------
    yline(ax, 0, '--', 'Color',[0.3 0.3 0.3], 'HandleVisibility','off');
    if opts.ShowSessions
        for i = 1:numel(S)
            plot(ax, S(i).trel, S(i).y, '-', 'Color',[0.6 0.6 0.6 0.35], ...
                 'LineWidth',0.5, 'HandleVisibility','off');
        end
    end
    M = [S.ycol];
    Q = rowQuantile(M, [0.25 0.50 0.75]);
    hBand = patch(ax, [tGrid; flipud(tGrid)], [Q(:,1); flipud(Q(:,3))], ...
                  [0.2 0.2 0.2], 'FaceAlpha',0.18, 'EdgeColor','none');
    hMed  = plot(ax, tGrid, Q(:,2), '-', 'Color',[0 0 0], 'LineWidth',2.2);
    legH  = [hMed, hBand];
    legTxt = {'median (pooled)', 'IQR (pooled)'};
    res.pooled = struct('q1',Q(:,1),'median',Q(:,2),'q3',Q(:,3));
else
    % -------- one band per player --------------------------------------
    res.perPlayer = struct('player',{},'playerName',{},'q1',{},'median',{},'q3',{},'nSessions',{});
    for p = 1:numel(uPlayers)
        pid = uPlayers{p};
        sel = strcmp({S.player}, pid);
        col = colourOf(pid);
        dNames = {S(sel).playerName};
        dName = dNames{1};
        if opts.ShowSessions
            for i = find(sel)
                plot(ax, S(i).trel, S(i).y, '-', 'Color',[col 0.25], ...
                     'LineWidth',0.5, 'HandleVisibility','off');
            end
        end
        M = [S(sel).ycol];
        Q = rowQuantile(M, [0.25 0.50 0.75]);
        % band only meaningful with >=2 sessions; still draw (zero-width if 1)
        patch(ax, [tGrid; flipud(tGrid)], [Q(:,1); flipud(Q(:,3))], col, ...
              'FaceAlpha',0.15, 'EdgeColor','none', 'HandleVisibility','off');
        hMed = plot(ax, tGrid, Q(:,2), '-', 'Color',col, 'LineWidth',2);
        legH(end+1)  = hMed; %#ok<AGROW>
        legTxt{end+1} = sprintf('%s (n=%d)', dName, nnz(sel)); %#ok<AGROW>
        res.perPlayer(end+1) = struct('player',pid, 'playerName',dName, 'q1',Q(:,1), ...
            'median',Q(:,2), 'q3',Q(:,3), 'nSessions',nnz(sel)); %#ok<AGROW>
    end
end

xlim(ax, [0 Ttrunc]);
xlabel(ax, 'session time (s)');
if opts.Detrend
    ylabel(ax, 'd - d_{ss}  (difficulty, detrended)');
else
    ylabel(ax, 'd  (difficulty)');
end
title(ax, sprintf('%s controller — difficulty over time', label));
subtitle(ax, sprintf(['%d sessions, %d players   |   band truncated to shortest ' ...
    'session (%.0f s)%s'], numel(S), numel(uPlayers), Ttrunc, ...
    ternary(opts.Detrend, '   |   pooled, detrended', '   |   per-player IQR')));
legend(ax, legH, legTxt, 'Location','best', 'Box','off');

res.label = label;
res.grid = tGrid;
res.Ttrunc = Ttrunc;
res.nSessions = numel(S);
res.nPlayers = numel(uPlayers);
end


% ========================================================================
function Q = rowQuantile(M, ps)
% Per-row quantiles across columns, NaN-ignoring, no Statistics Toolbox.
% Type-7 (linear interpolation of order statistics; numpy/R default).
%   M  : nRows x nCols
%   ps : 1 x k probabilities in [0,1]
%   Q  : nRows x k
nR = size(M,1); k = numel(ps);
Q = nan(nR, k);
for i = 1:nR
    v = M(i, :);
    v = v(isfinite(v));
    n = numel(v);
    if n == 0, continue; end
    v = sort(v);
    if n == 1, Q(i, :) = v; continue; end
    pos  = ps*(n-1) + 1;            % 1..n
    lo   = floor(pos); hi = ceil(pos);
    frac = pos - lo;
    Q(i, :) = v(lo).*(1-frac) + v(hi).*frac;
end
end


% ========================================================================
function C = distinctColours(n)
% n visually distinct RGB rows without needing a toolbox. Uses a fixed
% qualitative base palette, extended by golden-ratio hue spacing if needed.
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
function v = ternary(c, a, b)
if c, v = a; else, v = b; end
end
