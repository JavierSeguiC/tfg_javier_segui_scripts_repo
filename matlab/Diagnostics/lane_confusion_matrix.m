function out = lane_confusion_matrix(root, varargin)
%LANE_CONFUSION_MATRIX  True lane vs pressed lane, from WrongLane errors.
%
%   out = LANE_CONFUSION_MATRIX(root) builds a 4x4 confusion matrix (true
%   note lane -> lane actually pressed) per player, plus one pooled across
%   the whole population, to surface finger-individuation patterns (e.g. a
%   consistent ring-finger-pressed-for-pinky-note confusion).
%
%   IMPORTANT — READ BEFORE TRUSTING THE OFF-DIAGONAL NUMBERS:
%   noteOutcomes_*.csv logs the note's TRUE lane and a correctLane boolean,
%   but NOT which lane was actually pressed when outcome=="WrongLane". That
%   information isn't in the schema at all -- SessionRecorder never wrote
%   it. So the pressed lane is RECONSTRUCTED here by time-matching against
%   inputProfiles_*.csv (the raw press log). This is a heuristic, not
%   logged ground truth. The proper fix, if this diagnostic turns out to
%   matter for the thesis, is to have NoteResolver log the actually-matched
%   press's lane (or its inputProfiles eventId) directly onto
%   NoteOutcomeEvent/noteOutcomes.csv -- exact instead of reconstructed.
%
%   RECONSTRUCTION, TWO CASES:
%   * Standalone notes (chordSize<=1): unambiguous. A WrongLane note has
%     exactly one candidate true lane, so the nearest off-lane press within
%     [tEnter,tExit] (+/- MatchSlack) is taken as the culprit and counted
%     directly at (trueLane, pressedLane).
%   * Chords (chordSize>1): handled PER CHORD, not per note (grouped by
%     chordId), because a naive per-note search would double-count the same
%     stray press once for every WrongLane member sharing the chord's
%     window. For each chord, chordLanes = the set of lanes actually
%     carrying one of its notes. Any press LANDING ON A CHORD LANE is
%     skipped -- it may legitimately be the correct press for a different
%     chord member, and that specific ambiguity can't be resolved from this
%     data. Any press on a lane OUTSIDE chordLanes (a lane with nothing on
%     it at all) is unambiguous evidence of an error -- the player pressed
%     an empty lane -- but WHICH chord member it was meant for is unknown,
%     so its weight is CONFOUNDED: split 1/|chordLanes| across
%     (each chord lane, pressed lane). This means off-diagonal cells can be
%     non-integer. Controlled by ConfoundChordErrors (default true); set
%     false to fall back to excluding chords entirely instead.
%
%   The diagonal (correct-lane outcomes) is exact, not reconstructed --
%   correctLane already tells you pressed lane == true lane.
%
%   COLOR SCALE: the diagonal is typically ~100x the off-diagonal cells (a
%   session with hundreds of correct hits and a handful of wrong-lane
%   errors), so including it when setting the color range crushes every
%   off-diagonal cell to the same near-zero color. The diagonal is
%   therefore EXCLUDED from the color-scale computation and rendered as a
%   fixed neutral color instead of through the colormap -- the color range
%   is spent entirely on distinguishing the off-diagonal error pattern,
%   which is the point of this figure.
%
%   The darkest color value is SHARED across every figure in a run: it's
%   the single worst off-diagonal row% cell found across ALL players (see
%   sharedClimHi), not each figure's own max. This puts every player's
%   matrix -- and the pooled POPULATION matrix -- on the same scale, so
%   colors are directly comparable player-to-player instead of each figure
%   auto-stretching its own weakest signal to look equally "dark red".
%
%   No controller filtering: unlike the control-loop scripts, this is a
%   player motor-control question, not a controller-performance one, so
%   ALL sessions (PI, Rule-based, Manual) are pooled to maximize data.
%
%   out = LANE_CONFUSION_MATRIX(root, Name, Value, ...) accepts:
%     ConfoundChordErrors (true) reconstruct chord empty-lane errors as
%                          described above. false = exclude chord notes
%                          entirely (old conservative behaviour).
%     MatchSlack    (0.15)  seconds of padding added on both sides of the
%                          relevant [tEnter,tExit] window when searching
%                          for a culprit press.
%     Plot          (true)  draw one heatmap figure per player + one
%                          population figure.
%     Quiet         (true)  suppress per-folder missing-file warnings.
%
%   RETURNS out, a struct with:
%     .perPlayer   struct array, one per player: profileId (GUID, stable
%                  grouping key), displayName (sessionMeta 'name', used for
%                  figures/titles), counts (4x4, rows=true lane, cols=
%                  pressed lane, both now FINGER-indexed post handedness-
%                  remap; off-diagonal entries may be non-integer, see chord
%                  confounding above), rowPct (row-normalized %, NaN row if
%                  that true lane had 0 events).
%     .population  same fields, pooled across all players.
%     .attribution table, one row per session: folder, profileId,
%                  nStandaloneWrongLane, nStandaloneAttributed,
%                  nStandaloneUnattributed, nChordGroups,
%                  nChordEmptyPressesAttributed, nChordAmbiguousSkipped --
%                  the transparency numbers for the reconstruction. LOOK AT
%                  THIS before over-reading the off-diagonal pattern.
%
%   Requires: load_recording.m, build_session_index.m on the path. Base
%   MATLAB only otherwise (no Image/Statistics Toolbox). R2024b baseline.

p = inputParser;
p.addParameter('ConfoundChordErrors', true,  @(x)islogical(x)||isnumeric(x));
p.addParameter('MatchSlack',          0.15,  @(x)isnumeric(x)&&isscalar(x)&&x>=0);
p.addParameter('Plot',                true,  @(x)islogical(x)||isnumeric(x));
p.addParameter('Quiet',               true,  @(x)islogical(x)||isnumeric(x));
p.parse(varargin{:});
opt = p.Results;
opt.ConfoundChordErrors = logical(opt.ConfoundChordErrors);
opt.Plot  = logical(opt.Plot);
opt.Quiet = logical(opt.Quiet);

idx = build_session_index(root, 'HeavyStats', false, 'Quiet', opt.Quiet);
if height(idx) == 0
    warning('lane_confusion_matrix:noSessions', 'No sessions found under %s', root);
    out = emptyOut(); return;
end
fprintf('Building lane confusion matrices over %d session(s) (all controllers)...\n', height(idx));

[players, ~, playerGroups] = unique(idx.profileId, 'stable');
numPlayers = numel(players);

attribRows = cell(0,8);
perPlayer = struct('profileId',{},'displayName',{},'counts',{},'rowPct',{});
popCounts = zeros(4,4);

for pIdx = 1:numPlayers
    pName = players(pIdx);
    if ismissing(pName) || pName == "", pName = "Unknown"; end
    pSessions = idx(playerGroups == pIdx, :);

    % Display name (from sessionMeta's own 'name' field) — profileId (a
    % GUID) is kept as the stable grouping key but never shown in a figure.
    displayName = pSessions.name_(1);
    if ismissing(displayName) || displayName == "", displayName = pName; end

    playerCounts = zeros(4,4);

    for s = 1:height(pSessions)
        folder = char(pSessions.folder(s));
        try
            rec = load_recording(folder, 'Streams', {'meta','notes','presses'}, 'Quiet', opt.Quiet);
        catch ME
            warning('lane_confusion_matrix:skip', 'Skipping %s: %s', folder, ME.message);
            continue;
        end
        if isempty(rec.notes)
            continue;
        end

        [C, d] = processSession(rec, opt);
        playerCounts = playerCounts + C;
        popCounts = popCounts + C;

        attribRows(end+1,:) = { string(folder), string(pName), d.nStandaloneWrongLane, ...
            d.nStandaloneAttributed, d.nStandaloneUnattributed, d.nChordGroups, ...
            d.nChordEmptyPressesAttributed, d.nChordAmbiguousSkipped }; %#ok<AGROW>
    end

    perPlayer(end+1) = struct('profileId', pName, 'displayName', displayName, ...
        'counts', playerCounts, 'rowPct', rowNormalize(playerCounts)); %#ok<AGROW>
end

attributionT = cell2table(attribRows, 'VariableNames', ...
    {'folder','profileId','nStandaloneWrongLane','nStandaloneAttributed', ...
     'nStandaloneUnattributed','nChordGroups','nChordEmptyPressesAttributed','nChordAmbiguousSkipped'});

fprintf(['\nStandalone: %d/%d WrongLane notes attributed to a pressed lane.\n' ...
         'Chords: %d chord groups processed, %d empty-lane presses confounded ' ...
         'across chord members, %d ambiguous presses skipped (landed on a chord''s own lane).\n'], ...
    sum(attributionT.nStandaloneAttributed), sum(attributionT.nStandaloneWrongLane), ...
    sum(attributionT.nChordGroups), sum(attributionT.nChordEmptyPressesAttributed), ...
    sum(attributionT.nChordAmbiguousSkipped));

population.profileId = "POPULATION";
population.displayName = "POPULATION";
population.counts = popCounts;
population.rowPct = rowNormalize(popCounts);

if opt.Plot
    % SHARED COLOR SCALE: darkest color = the single worst off-diagonal
    % row% found across ALL players (not each figure's own max), so every
    % player's matrix -- and the pooled POPULATION matrix -- sit on the
    % same scale and are directly comparable to each other. Without this,
    % each figure auto-scales to its own worst cell, so a player with a
    % single severe confusion and a player with a mild one can look
    % identically "dark red" despite very different absolute error rates.
    globalClimHi = sharedClimHi(perPlayer);

    for pIdx = 1:numel(perPlayer)
        drawConfusion(perPlayer(pIdx).counts, perPlayer(pIdx).rowPct, ...
            sprintf('Lane confusion — %s', perPlayer(pIdx).displayName), globalClimHi);
    end
    drawConfusion(population.counts, population.rowPct, ...
        'Lane confusion — POPULATION (all players)', globalClimHi);
end

out.perPlayer   = perPlayer;
out.population  = population;
out.attribution = attributionT;
end % ===================== end main =====================================


% ========================================================================
function [C, d] = processSession(rec, opt)
% One session's contribution to the 4x4 confusion matrix (rows=true lane,
% cols=pressed lane), plus attribution diagnostics.
C = zeros(4,4);
d = struct('nStandaloneWrongLane',0,'nStandaloneAttributed',0,'nStandaloneUnattributed',0, ...
           'nChordGroups',0,'nChordEmptyPressesAttributed',0,'nChordAmbiguousSkipped',0);

N = rec.notes;
need = {'lane','outcome','correctLane','t_enter','t_exit'};
if ~all(ismember(need, N.Properties.VariableNames)), return; end

trueLane = double(N.lane);
outcome  = lower(string(N.outcome));
correctLane = logical(N.correctLane);
tEnter = double(N.t_enter);
tExit  = double(N.t_exit);
hasChordId   = ismember('chordId',   N.Properties.VariableNames);
hasChordSize = ismember('chordSize', N.Properties.VariableNames);
if hasChordSize, chordSize = double(N.chordSize); else, chordSize = ones(height(N),1); end
if hasChordId,   chordId   = double(N.chordId);   else, chordId   = -ones(height(N),1); end

% ---- Diagonal: exact, any correct-lane outcome with a real press match. --
matchedOutcomes = ["hit","forceinsufficient","underheld","earlypress","latepress"];
diagMask = correctLane & ismember(outcome, matchedOutcomes) & isfinite(trueLane) & ...
           trueLane >= 0 & trueLane <= 3;
lanesHit = trueLane(diagMask);
for l = 0:3
    C(l+1, l+1) = C(l+1, l+1) + nnz(lanesHit == l);
end

presses = rec.presses;
havePresses = ~isempty(presses) && all(ismember({'tPress','lane'}, presses.Properties.VariableNames));
if havePresses
    pLane = double(presses.lane);
    pTime = double(presses.tPress);
end

% ---- Standalone WrongLane notes: unambiguous, nearest-press attribution --
standaloneMask = (outcome == "wronglane") & isfinite(trueLane) & trueLane >= 0 & trueLane <= 3 & ...
                 (chordSize <= 1);
standaloneIdx = find(standaloneMask);
d.nStandaloneWrongLane = numel(standaloneIdx);

for k = 1:numel(standaloneIdx)
    i = standaloneIdx(k);
    tl = trueLane(i);
    if ~havePresses || ~isfinite(tEnter(i)) || ~isfinite(tExit(i))
        d.nStandaloneUnattributed = d.nStandaloneUnattributed + 1;
        continue;
    end
    lo = tEnter(i) - opt.MatchSlack;
    hi = tExit(i)  + opt.MatchSlack;
    cand = find(pLane ~= tl & pLane >= 0 & pLane <= 3 & pTime >= lo & pTime <= hi);
    if isempty(cand)
        d.nStandaloneUnattributed = d.nStandaloneUnattributed + 1;
        continue;
    end
    [~, best] = min(abs(pTime(cand) - tEnter(i)));
    pressedLane = pLane(cand(best));
    C(tl+1, pressedLane+1) = C(tl+1, pressedLane+1) + 1;
    d.nStandaloneAttributed = d.nStandaloneAttributed + 1;
end

% ---- Chords: grouped by chordId, confounded empty-lane attribution -------
if opt.ConfoundChordErrors && havePresses
    chordMask = (chordSize > 1) & isfinite(trueLane) & trueLane >= 0 & trueLane <= 3;
    if hasChordId
        ids = unique(chordId(chordMask));
        ids = ids(ids >= 0);   % -1 = not a chord member; shouldn't appear here but guard anyway
    else
        ids = [];
    end

    for c = 1:numel(ids)
        members = find(chordId == ids(c) & chordMask);
        if isempty(members), continue; end
        chordLanes = unique(trueLane(members));
        chordLanes = chordLanes(chordLanes >= 0 & chordLanes <= 3);
        if isempty(chordLanes), continue; end

        winLo = min(tEnter(members)) - opt.MatchSlack;
        winHi = max(tExit(members))  + opt.MatchSlack;
        if ~isfinite(winLo) || ~isfinite(winHi), continue; end

        inWindow = find(pTime >= winLo & pTime <= winHi & pLane >= 0 & pLane <= 3);
        if isempty(inWindow), continue; end
        d.nChordGroups = d.nChordGroups + 1;

        for pk = inWindow(:)'
            pl = pLane(pk);
            if ismember(pl, chordLanes)
                % Lands on one of the chord's own lanes: may legitimately be
                % the correct press for a different member -- unresolvable
                % ambiguity from this data, skip rather than guess.
                d.nChordAmbiguousSkipped = d.nChordAmbiguousSkipped + 1;
                continue;
            end
            % Empty lane: unambiguous error, unknown which member it was
            % meant for -- confound fractionally across all chord lanes.
            w = 1 / numel(chordLanes);
            for cl = chordLanes(:)'
                C(cl+1, pl+1) = C(cl+1, pl+1) + w;
            end
            d.nChordEmptyPressesAttributed = d.nChordEmptyPressesAttributed + 1;
        end
    end
end
end


% ========================================================================
function cmap = ylorrd(n)
% ColorBrewer "YlOrRd" (Yellow-Orange-Red) sequential palette, built from
% its standard 9-class control points and linearly interpolated to n rows.
% Not a MATLAB built-in colormap name, so constructed by hand -- base
% MATLAB only (interp1), no toolbox.
if nargin < 1, n = 256; end
anchors = [ ...
    255 255 204;  % #ffffcc
    255 237 160;  % #ffeda0
    254 217 118;  % #fed976
    254 178  76;  % #feb24c
    253 141  60;  % #fd8d3c
    252  78  42;  % #fc4e2a
    227  26  28;  % #e31a1c
    189   0  38;  % #bd0026
    128   0  38] / 255;  % #800026
k = size(anchors,1);
xi = linspace(0,1,k)';
xq = linspace(0,1,n)';
cmap = [interp1(xi, anchors(:,1), xq), interp1(xi, anchors(:,2), xq), interp1(xi, anchors(:,3), xq)];
end


% ========================================================================
function climHi = sharedClimHi(perPlayer)
% Population-wide darkest color-scale value: the max off-diagonal row%
% cell across every player's matrix. Same degenerate-case fallback as the
% old per-figure computation (no off-diagonal data anywhere -> clim=1,
% avoids a zero-width clim).
allOff = [];
for pIdx = 1:numel(perPlayer)
    rowPct = perPlayer(pIdx).rowPct;
    offDiagMask = ~eye(4,4,'logical');
    v = rowPct(offDiagMask);
    allOff = [allOff; v(~isnan(v))]; %#ok<AGROW>
end
if isempty(allOff) || max(allOff) <= 0
    climHi = 1;
else
    climHi = max(allOff);
end
end


% ========================================================================
function pct = rowNormalize(C)
rowSum = sum(C,2);
pct = 100 * C ./ rowSum;
pct(rowSum == 0, :) = NaN;
end


% ========================================================================
function out = emptyOut()
out.perPlayer   = struct('profileId',{},'displayName',{},'counts',{},'rowPct',{});
out.population  = struct('profileId',"POPULATION",'displayName',"POPULATION",'counts',zeros(4,4),'rowPct',nan(4,4));
out.attribution = table();
end


% ========================================================================
function drawConfusion(counts, rowPct, titleStr, climHi)
% Heatmap coloured by row-normalized %, with the DIAGONAL EXCLUDED from the
% color-scale computation and rendered as a fixed neutral colour instead --
% otherwise the (typically much larger) diagonal crushes every off-diagonal
% cell to the same near-zero colour, hiding the error pattern that's the
% actual point of this figure. Base MATLAB only (imagesc/colormap are core
% graphics, no Image Toolbox functions used).
%
% climHi is passed in by the caller (see sharedClimHi) so every figure in
% a run uses the SAME darkest-color value -- the population-wide worst
% off-diagonal cell -- rather than each matrix auto-scaling to its own max.
fig = figure('Color','w', 'Name',titleStr, 'Position',[100 100 480 460]);
ax = axes(fig); %#ok<LAXES>

dispData = rowPct;
alphaData = ~isnan(rowPct) & ~eye(4,4,'logical');   % diagonal + empty rows rendered as background

imagesc(ax, dispData, 'AlphaData', alphaData);
set(ax, 'Color', [0.80 0.80 0.80]);   % shows through where AlphaData=0 (diagonal + empty rows)
colormap(ax, ylorrd(256));
cb = colorbar(ax); ylabel(cb, 'row %  (OFF-DIAGONAL scale; diagonal excluded)');
clim(ax, [0 climHi]);

FINGER_NAMES = {'Index','Middle','Ring','Pinky'};   % canonical, post-remap (see load_recording.m)
ax.XTick = 1:4; ax.YTick = 1:4;
ax.XTickLabel = FINGER_NAMES;
ax.YTickLabel = FINGER_NAMES;
xlabel(ax, 'pressed finger');
ylabel(ax, 'true (note) finger');
title(ax, titleStr, 'Interpreter','none');
axis(ax, 'square');

for r = 1:4
    for c = 1:4
        n = counts(r,c);
        pctVal = rowPct(r,c);
        isDiag = (r == c);
        if n == round(n)
            nStr = sprintf('%d', n);
        else
            nStr = sprintf('%.2f', n);   % fractional, from chord confounding
        end
        if isnan(pctVal)
            txt = nStr;
        else
            txt = sprintf('%s\n(%.1f%%)', nStr, pctVal);
        end
        if isDiag
            txtCol = [0.15 0.15 0.15];   % fixed-background diagonal: always dark text
        elseif ~isnan(pctVal) && pctVal > 0.65*climHi
            txtCol = [1 1 1];
        else
            txtCol = [0 0 0];
        end
        text(ax, c, r, txt, 'HorizontalAlignment','center', 'VerticalAlignment','middle', ...
            'Color', txtCol, 'FontSize', 9);
    end
end
end
