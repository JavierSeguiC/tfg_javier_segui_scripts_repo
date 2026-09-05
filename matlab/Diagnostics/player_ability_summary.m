function summaryTable = player_ability_summary(rootFolder)
%PLAYER_ABILITY_SUMMARY Baseline reflex and per-finger force capacity per player.
%
%   T = PLAYER_ABILITY_SUMMARY(rootFolder) analyses every session under
%   rootFolder and produces one figure per player (note-outcome pie chart,
%   timing-error histogram, and a text summary panel of capacities +
%   trends), plus a returned MATLAB table with one row per player.
%
%   This is a thin USER of the shared analysis engine
%   (load_recording -> build_loop_signals -> compute_loop_metrics), the
%   same one evaluate_control_loops.m and aggregate_loop_metrics.m use --
%   it does not recompute stabilization, bias, or steady-state bands
%   itself, so "steady state" means exactly the same thing everywhere.
%
%   METHOD
%   * Reflex capacity (per session) = compute_loop_metrics's uMeanSS /
%     uMedianSS for loop 1: the mean/median difficulty d over
%     [t_stab, end], restricted to samples where e_dot itself is inside its
%     own steady band (so a momentary lapse doesn't drag the average).
%     NaN for a session that never stabilized.
%   * Force capacity (per session, per lane) = the TRUE applied force,
%     tau_ss + M_F_ss, i.e. compute_loop_metrics's uMeanSS/uMedianSS
%     (steady-state tau) PLUS muSS (the loop's own measured steady-state
%     margin -- NOT the target r, since a biased loop's real margin can
%     differ from its target).
%   * Player-level aggregation: ONE capacity number per session (not pooled
%     raw 10 Hz samples, which would both over-weight long sessions and be
%     heavily autocorrelated), then MEDIAN + IQR (Q1-Q3) across a player's
%     sessions, alongside the mean for comparison.
%   * Note-outcome pie chart and timing-error histogram pool the WHOLE
%     session (not restricted to post-stabilization) -- deliberately a
%     different scope from the capacity numbers, since overall play
%     accuracy and reaction timing aren't loop-state-dependent the way
%     steady-state capacity is.
%   * Timing error = tPress - tEnter (s); a negative median = early bias, a
%     positive median = late bias.
%   * Control-action trends are compute_loop_metrics's own uTrend (OLS
%     slope over the post-stabilization region, autocorrelation-corrected)
%     for each of the 5 loops -- reported as-is (numeric + significance),
%     with no fatigue/learning interpretation attached here.
%
%   USAGE:
%       T = player_ability_summary('path/to/Recordings');
%
%   Requires: load_recording.m, build_session_index.m, build_loop_signals.m,
%   compute_loop_metrics.m on the path. Base MATLAB only otherwise.
%   R2024b baseline.

arguments
    rootFolder (1,:) char = pwd
end

idx = build_session_index(rootFolder, 'HeavyStats', false, 'Quiet', true);
if height(idx) == 0
    fprintf('No sessions found in %s. Exiting.\n', rootFolder);
    summaryTable = table();
    return;
end

[players, ~, playerGroups] = unique(idx.profileId, 'stable');
numPlayers = numel(players);
fprintf('Found %d sessions across %d player(s).\n', height(idx), numPlayers);

playerIDs      = strings(numPlayers, 1);
playerNames    = strings(numPlayers, 1);
nSessions      = zeros(numPlayers, 1);
hitRateOverall = nan(numPlayers, 1);
timingErrMed   = nan(numPlayers, 1);

reflexCapMean = nan(numPlayers, 1);
reflexCapMed  = nan(numPlayers, 1);
reflexCapIQR  = nan(numPlayers, 1);
nReflexStab   = zeros(numPlayers, 1);
forceCapMean  = nan(numPlayers, 4);
forceCapMed   = nan(numPlayers, 4);
forceCapIQR   = nan(numPlayers, 4);
nForceStab    = zeros(numPlayers, 4);

buildOpt = struct('ReflexOnly', false);   % use build_loop_signals's own defaults otherwise

for p = 1:numPlayers
    pName = players(p);
    if ismissing(pName) || pName == "", pName = "Unknown"; end
    playerIDs(p) = pName;

    pSessions = idx(playerGroups == p, :);
    nSessions(p) = height(pSessions);

    % Display name (sessionMeta 'name') for figures/printouts — profileId
    % (a GUID) is kept as the grouping key but never shown to a human.
    dName = pSessions.name_(1);
    if ismissing(dName) || dName == "", dName = pName; end
    playerNames(p) = dName;

    fprintf('Processing player: %s (%d session(s))...\n', dName, nSessions(p));

    allOutcomes     = strings(0,1);
    allTimingErrors = [];

    % ONE capacity value per session (not pooled raw samples).
    reflexCapSess = nan(height(pSessions), 1);
    forceCapSess  = nan(height(pSessions), 4);
    trendStrings  = cell(height(pSessions), 5);

    for s = 1:height(pSessions)
        folder = char(pSessions.folder(s));
        rec = load_recording(folder, 'Streams', {'meta','notes','control'}, 'Quiet', true);

        if ~isempty(rec.notes)
            oc = string(rec.notes.outcome);
            oc = oc(oc ~= "" & ~ismissing(oc));
            allOutcomes = [allOutcomes; oc]; %#ok<AGROW>

            if ismember('timingError', rec.notes.Properties.VariableNames)
                te = rec.notes.timingError;
                allTimingErrors = [allTimingErrors; te(isfinite(te))]; %#ok<AGROW>
            end
        end

        if isempty(rec.control)
            continue;   % no control stream: nothing more to extract from this session
        end

        [loops, t] = build_loop_signals(rec, buildOpt);

        % ---- Loop 1: reflex capacity = uMeanSS/uMedianSS of d ----------
        m1 = compute_loop_metrics(t, loops(1).y, loops(1).u, loops(1).r);
        if m1.stabilized && isfinite(m1.uMeanSS)
            reflexCapSess(s) = m1.uMeanSS;
        end
        trendStrings{s,1} = formatTrend(m1, 'units/min');

        % ---- Loops 2-5: force capacity = tau_ss + M_F_ss ----------------
        for L = 2:min(5, numel(loops))
            lane = L - 1;
            mL = compute_loop_metrics(t, loops(L).y, loops(L).u, loops(L).r);
            if mL.stabilized && isfinite(mL.uMeanSS) && isfinite(mL.muSS)
                forceCapSess(s, lane) = mL.uMeanSS + mL.muSS;
            end
            trendStrings{s, L} = formatTrend(mL, '/min', 4);
        end
    end

    % ---- Aggregate across this player's sessions: mean, median, IQR ----
    reflexCapMean(p) = mean(reflexCapSess, 'omitnan');
    reflexCapMed(p)  = median(reflexCapSess, 'omitnan');
    reflexCapIQR(p)  = iqrNoToolbox(reflexCapSess);
    nReflexStab(p)   = nnz(~isnan(reflexCapSess));

    for lane = 1:4
        col = forceCapSess(:, lane);
        forceCapMean(p, lane) = mean(col, 'omitnan');
        forceCapMed(p, lane)  = median(col, 'omitnan');
        forceCapIQR(p, lane)  = iqrNoToolbox(col);
        nForceStab(p, lane)   = nnz(~isnan(col));
    end

    if ~isempty(allTimingErrors)
        timingErrMed(p) = median(allTimingErrors);
    end
    if ~isempty(allOutcomes)
        hitRateOverall(p) = sum(lower(allOutcomes) == "hit") / numel(allOutcomes);
    end

    drawPlayerFigure(dName, allOutcomes, allTimingErrors, timingErrMed(p), ...
        reflexCapMean(p), reflexCapMed(p), nReflexStab(p), ...
        forceCapMean(p,:), forceCapMed(p,:), nForceStab(p,:), ...
        pSessions, trendStrings);
end

summaryTable = table(playerIDs, playerNames, nSessions, hitRateOverall, timingErrMed, ...
    reflexCapMean, reflexCapMed, reflexCapIQR, nReflexStab, ...
    forceCapMean, forceCapMed, forceCapIQR, nForceStab, ...
    'VariableNames', {'PlayerID','PlayerName','NSessions','OverallHitRate','MedianTimingErr', ...
    'MeanReflexCap_d','MedianReflexCap_d','IQRReflexCap_d','NReflexStabilized', ...
    'MeanForceCap_Index_Pinky','MedianForceCap_Index_Pinky','IQRForceCap_Index_Pinky','NForceStabilized_Index_Pinky'});

disp(summaryTable);
end % ===================== end main =====================================


% ========================================================================
function drawPlayerFigure(pName, allOutcomes, allTimingErrors, tMed, ...
    reflexMean, reflexMed, nReflexStab, forceMean, forceMed, nForceStab, pSessions, trendStrings)

fig = figure('Name', sprintf('Player Profile: %s', pName), 'Color', 'w', ...
             'Position', [100, 100, 1200, 500]);
tlo = tiledlayout(fig, 1, 3, 'Padding', 'compact', 'TileSpacing', 'compact');
title(tlo, sprintf('Ability Summary — Player: %s', pName), ...
    'FontWeight', 'bold', 'FontSize', 14, 'Interpreter', 'none');

% Tile 1: Note Outcomes Pie Chart (whole session, all sessions pooled)
ax1 = nexttile(tlo);
if ~isempty(allOutcomes)
    pie(ax1, categorical(allOutcomes));
    title(ax1, 'Note Outcomes (whole session)');
else
    axis(ax1, 'off');
    title(ax1, 'No Outcome Data');
end

% Tile 2: Timing Error Distribution (whole session, all sessions pooled)
ax2 = nexttile(tlo);
if ~isempty(allTimingErrors)
    histogram(ax2, allTimingErrors, 'BinWidth', 0.05, 'FaceColor', [0 0.45 0.74], 'EdgeColor', 'w');
    hold(ax2, 'on');
    xline(ax2, tMed, 'r-', 'LineWidth', 2);
    biasTxt = "Neutral";
    if tMed < -0.05, biasTxt = "Early Bias"; elseif tMed > 0.05, biasTxt = "Late Bias"; end
    title(ax2, sprintf('Timing Error (Median: %.3fs)\n[%s]', tMed, biasTxt));
    xlabel(ax2, 'Timing Error [s]  (tPress - tEnter)');
    ylabel(ax2, 'Count');
    grid(ax2, 'on');
else
    axis(ax2, 'off');
    title(ax2, 'No Timing Data');
end

% Tile 3: Text Summary Panel (capacities & trends)
ax3 = nexttile(tlo);
axis(ax3, 'off');

txt = sprintf('STEADY-STATE CAPACITIES (per-session, median/IQR across sessions)\n');
txt = sprintf('%s--------------------------------------------------------------\n', txt);
txt = sprintf('%sReflex (Difficulty d)  [n=%d stabilized]:\n  Mean: %.1f  |  Median: %.1f\n\n', ...
    txt, nReflexStab, reflexMean, reflexMed);
FINGER_NAMES = {'Index','Middle','Ring','Pinky'};   % canonical, post-remap (see load_recording.m)
txt = sprintf('%sForce (tau_ss + M_F_ss = true applied force):\n', txt);
for lane = 1:4
    txt = sprintf('%s  %s [n=%d]: Mean: %.3f | Med: %.3f\n', txt, FINGER_NAMES{lane}, nForceStab(lane), forceMean(lane), forceMed(lane));
end

txt = sprintf('%s\nCONTROL ACTION TRENDS (post-stabilization)\n', txt);
txt = sprintf('%s-------------------------------------------\n', txt);
for s = 1:height(pSessions)
    txt = sprintf('%sSession %d:\n', txt, s);
    txt = sprintf('%s  Reflex : %s\n', txt, trendStrings{s, 1});
    for lane = 1:4
        txt = sprintf('%s  %s: %s\n', txt, FINGER_NAMES{lane}, trendStrings{s, lane+1});
    end
end

text(ax3, 0, 1, txt, 'Units', 'normalized', 'VerticalAlignment', 'top', ...
    'FontName', 'Courier New', 'FontSize', 9, 'Interpreter', 'none');
end


% ========================================================================
function s = formatTrend(m, unitStr, precision)
if nargin < 3, precision = 2; end   % reflex trends (units/min, ~O(1-10)) keep the old default
if ~m.stabilized
    s = 'Not stabilized';
    return;
end
if isfinite(m.uTrend)
    sig = ternary(m.uTrendSignificant, 'SIGNIFICANT', 'n.s.');
    fmt = sprintf('%%+.%df %%s (%%s)', precision);
    s = sprintf(fmt, m.uTrend, unitStr, sig);
else
    s = 'n/a';
end
end


% ========================================================================
function v = ternary(c, a, b)
if c, v = a; else, v = b; end
end


% ========================================================================
function w = iqrNoToolbox(x)
% Q3 - Q1 (type-7 quantile, matching numpy/R default), NaN-omitting, no
% Statistics Toolbox dependency.
x = x(~isnan(x));
n = numel(x);
if n < 2, w = NaN; return; end
x = sort(x);
q = quantileNT(x, [0.25 0.75]);
w = q(2) - q(1);
end

function q = quantileNT(xsorted, ps)
n = numel(xsorted);
q = nan(size(ps));
if n == 0, return; end
if n == 1, q(:) = xsorted(1); return; end
pos = ps*(n-1) + 1;
lo = floor(pos); hi = ceil(pos); frac = pos - lo;
lo = min(max(lo,1),n); hi = min(max(hi,1),n);
q = xsorted(lo).*(1-frac) + xsorted(hi).*frac;
end
