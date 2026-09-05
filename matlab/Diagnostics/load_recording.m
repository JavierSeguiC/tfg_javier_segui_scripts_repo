function rec = load_recording(folder, opts)
%LOAD_RECORDING  Load one DDA session recording folder into a single struct.
%
%   rec = LOAD_RECORDING(folder) reads the five CSVs written by
%   SessionRecorder.cs for one session and returns them, parsed and
%   lightly normalised, in one struct. This is the single place CSV
%   parsing / column-name tolerance / Unity-bool handling lives, so every
%   analysis script calls this instead of re-solving those problems.
%
%   rec = LOAD_RECORDING(folder, opts) accepts a struct of options:
%     opts.Streams   cellstr subset of {'meta','notes','control','presses',
%                    'raw'} to load (default: all). Skipping the big
%                    continuous streams ('raw','control') is much faster
%                    when a script only needs, say, meta + notes.
%     opts.Quiet     true to suppress the "missing file" warnings for
%                    optional streams (default false).
%
%   The five files (per SessionRecorder.cs), matched case-insensitively by
%   prefix inside `folder`:
%     sessionMeta_*.csv    one row  -> rec.meta   (struct, scalar fields)
%     noteOutcomes_*.csv   per note -> rec.notes  (table)
%     controlAction_*.csv  ~10 Hz   -> rec.control (table)
%     inputProfiles_*.csv  per press-> rec.presses (table)
%     rawInputs_*.csv      ~60 Hz   -> rec.raw    (table)
%
%   All streams share ONE clock: t_session (seconds, pause-aware), written
%   by SessionRecorder. noteOutcomes carries a precomputed t_session column
%   (= SessionTimeOf(tEnter)); its raw t_enter/t_exit (Unity Time.time) are
%   also kept but should NOT be mixed with t_session.
%
%   Returned struct fields:
%     rec.folder        char, the folder path
%     rec.name          char, the recording folder name
%     rec.stamp         char, <userSlug>_<yyyyMMdd>_<HHmm> if parseable
%     rec.meta          struct (see below) or [] if not loaded
%     rec.notes/control/presses/raw   tables or [] if not loaded/missing
%     rec.controllers   cellstr, split of meta.controllersUsed (';')
%     rec.hasPI/hasRuleBased/hasManual  logical convenience flags
%     rec.rForce/rReflex  the session's setpoints from meta (NaN if absent)
%     rec.playingHand    'Left'/'Right', resolved (assumed 'Right' if the
%                        session predates this field being logged)
%     rec.dominantHand   'Left'/'Right'/'' — descriptive only, does NOT
%                        drive the lane remap below
%     rec.fingerNames    {'Index','Middle','Ring','Pinky'} — canonical
%                        finger name for lane 0..3 AFTER the remap below
%
%   HANDEDNESS / LANE->FINGER CONVENTION (Aug 2026):
%   On screen, lane 0 is always the leftmost lane and lane 3 the rightmost,
%   regardless of playing hand. Which finger that corresponds to flips with
%   the hand: right hand -> lane0=Index,lane1=Middle,lane2=Ring,lane3=Pinky
%   (this is the canonical/on-screen order); left hand mirrors it exactly
%   (lane0=Pinky ... lane3=Index), i.e. finger = 3 - lane.
%   This function REMAPS every lane-indexed column so downstream code always
%   sees canonical (finger-based) lane indices — lane 0 is always Index,
%   lane 1 always Middle, etc. — regardless of which hand was used. Right-
%   hand sessions are already canonical and pass through unchanged; only
%   left-hand sessions are actually permuted. This is an ANALYSIS-layer
%   transform only: the CSVs on disk stay exactly as physically captured
%   (screen-position/physical-lane), and PlayingHand is logged right
%   alongside so the remap is always reversible/inspectable. Every script
%   downstream of this function only needs cosmetic "Lane N" -> finger-name
%   label changes, never remap logic of its own.
%   Recordings with no logged PlayingHand (pre-Aug-2026) are ASSUMED Right
%   (i.e. left as-is) — a deliberate choice, not a data-driven inference.
%
%   Requires base MATLAB only. Target R2019b+ (arguments block); R2024b baseline.

arguments
    folder (1,:) char
    opts.Streams (1,:) cell = {'meta','notes','control','presses','raw'}
    opts.Quiet (1,1) logical = false
end

if ~isfolder(folder)
    error('load_recording:badFolder', '%s is not a folder.', folder);
end

rec = struct();
rec.folder = folder;
[~, rec.name] = fileparts(strip_sep(folder));
rec.stamp  = stamp_from_name(rec.name);

want = @(s) any(strcmpi(opts.Streams, s));

% ---- sessionMeta (one row -> struct) ----------------------------------
rec.meta = [];
if want('meta')
    f = find_one(folder, 'sessionMeta_*.csv', opts.Quiet);
    if ~isempty(f)
        Tm = readtable(f, 'TextType','string');
        rec.meta = table_first_row_struct(Tm);
    end
end

% ---- the tabular streams ----------------------------------------------
rec.notes   = load_table(folder, 'noteOutcomes_*.csv',  want('notes'),   opts.Quiet);
rec.control = load_table(folder, 'controlAction_*.csv', want('control'), opts.Quiet);
rec.presses = load_table(folder, 'inputProfiles_*.csv', want('presses'), opts.Quiet);
rec.raw     = load_table(folder, 'rawInputs_*.csv',     want('raw'),     opts.Quiet);

% ---- handedness: resolve PlayingHand and remap lane-indexed columns ---
FINGER_NAMES = {'Index','Middle','Ring','Pinky'};
rec.fingerNames = FINGER_NAMES;

rec.playingHand = 'Right';    % assumed default for recordings predating this field
rec.dominantHand = '';
if ~isempty(rec.meta)
    if isfield(rec.meta, 'playingHand')
        h = strtrim(string(rec.meta.playingHand));
        if strcmpi(h, 'Left'), rec.playingHand = 'Left';
        elseif strcmpi(h, 'Right'), rec.playingHand = 'Right';
        % anything else (missing/blank/unrecognized) -> keep the Right default
        end
    end
    if isfield(rec.meta, 'dominantHand')
        rec.dominantHand = char(strtrim(string(rec.meta.dominantHand)));
    end
end

if strcmp(rec.playingHand, 'Left')
    rec.notes   = remap_lane_column(rec.notes,   'lane');
    rec.presses = remap_lane_column(rec.presses, 'lane');
    rec.raw     = reverse_lane_columns(rec.raw,     'f_lane%d',   0:3);
    rec.control = reverse_lane_columns(rec.control, 'tau%d',      0:3);
    rec.control = reverse_lane_columns(rec.control, 'M_F%d',      0:3);
end
% Right-hand (or assumed-Right) sessions are already canonical: lane == finger,
% nothing to permute.

% ---- convenience: controllers + setpoints from meta -------------------
rec.controllers = {};
rec.hasPI = false; rec.hasRuleBased = false; rec.hasManual = false;
rec.rReflex = NaN; rec.rForce = NaN;
if ~isempty(rec.meta)
    if isfield(rec.meta, 'controllersUsed')
        cu = string(rec.meta.controllersUsed);
        parts = strtrim(split(cu, ';'));
        parts(parts == "") = [];
        rec.controllers = cellstr(parts);
        low = lower(rec.controllers);
        % AuthorityName strings are like "PI Difficulty Controller",
        % "Rule-based ...", "Manual"/"Preset ...". Match on distinctive tokens;
        % anchor PI so it can't match an incidental "pi" inside another word.
        rec.hasPI         = any(startsWith(low, 'pi') | contains(low, 'pi difficulty'));
        rec.hasRuleBased  = any(contains(low, 'rule'));
        rec.hasManual     = any(contains(low, 'manual') | contains(low, 'preset'));
    end
    rec.rReflex = meta_num(rec.meta, 'targetErrorsPerMinute');
    rec.rForce  = meta_num(rec.meta, 'targetForceMargin');
end
end % ===================== end main =====================================


% ========================================================================
function T = load_table(folder, pattern, doLoad, quiet)
T = [];
if ~doLoad, return; end
f = find_one(folder, pattern, quiet);
if isempty(f), return; end
T = readtable(f, 'TextType','string');
T = normalize_bools(T);   % Unity "True"/"False" text -> logical
end


% ========================================================================
function f = find_one(folder, pattern, quiet)
% First file matching pattern in folder; warn (unless quiet) if none/many.
d = dir(fullfile(folder, pattern));
if isempty(d)
    if ~quiet
        warning('load_recording:missing', 'No %s in %s', pattern, folder);
    end
    f = ''; return;
end
if numel(d) > 1 && ~quiet
    warning('load_recording:multiple', ...
        'Multiple %s in %s; using %s', pattern, folder, d(1).name);
end
f = fullfile(d(1).folder, d(1).name);
end


% ========================================================================
function T = normalize_bools(T)
% Convert any column that is entirely Unity bool text ("True"/"False",
% case-insensitive, NaN/empty allowed) into a MATLAB logical column.
% correctLane and wasSimultaneous are the known cases, but this is generic.
for k = 1:width(T)
    col = T{:, k};
    if ~(isstring(col) || iscellstr(col)), continue; end
    s = lower(strtrim(string(col)));
    isTrue  = s == "true";
    isFalse = s == "false";
    isBlank = ismissing(s) | s == "" | s == "nan";
    if all(isTrue | isFalse | isBlank) && any(isTrue | isFalse)
        b = false(size(s));
        b(isTrue) = true;
        T.(T.Properties.VariableNames{k}) = b;
    end
end
end


% ========================================================================
function s = table_first_row_struct(T)
% Flatten a one-row table to a scalar struct (string stays string, numeric
% stays numeric). If somehow multi-row, takes the first row.
s = struct();
if height(T) == 0, return; end
vn = T.Properties.VariableNames;
for k = 1:numel(vn)
    v = T{1, k};
    if iscell(v), v = v{1}; end
    s.(vn{k}) = v;
end
end


% ========================================================================
function v = meta_num(meta, field)
% Numeric value of a meta field, tolerant of it being stored as string.
v = NaN;
if ~isfield(meta, field), return; end
raw = meta.(field);
if isnumeric(raw)
    v = double(raw);
else
    v = str2double(string(raw));
end
end


% ========================================================================
function T = remap_lane_column(T, colName)
% Mirrors a per-row lane-index VALUE column (0..3) for left-hand sessions:
% finger = 3 - lane. Used for notes.lane / presses.lane, where each row
% carries one lane number. NaN rows pass through untouched. No-op if the
% column isn't present (case-insensitive match), so this is safe to call
% even on tables loaded with a Streams subset that excludes this file.
if isempty(T), return; end
vn = T.Properties.VariableNames;
idx = find(strcmpi(vn, colName), 1);
if isempty(idx), return; end
col = T{:, idx};
if isnumeric(col)
    valid = ~isnan(col);
    col(valid) = 3 - col(valid);
    T{:, idx} = col;
end
end


% ========================================================================
function T = reverse_lane_columns(T, pattern, laneIdx)
% Reverses the COLUMN ORDER of a per-lane column group (e.g. f_lane0..
% f_lane3, tau0..tau3) in place: [c0 c1 c2 c3] -> [c3 c2 c1 c0]. This is
% the finger=3-lane mirror applied to column position instead of row
% value, for the continuous streams where each lane has its own column
% rather than a per-row lane index. No-op if any expected column is
% missing (case-insensitive match), so callers don't need to know which
% streams were actually loaded.
if isempty(T), return; end
vn = T.Properties.VariableNames;
names = arrayfun(@(i) sprintf(pattern, i), laneIdx, 'UniformOutput', false);
idxs = nan(1, numel(names));
for k = 1:numel(names)
    f = find(strcmpi(vn, names{k}), 1);
    if isempty(f), return; end
    idxs(k) = f;
end
T{:, idxs} = fliplr(T{:, idxs});
end


% ========================================================================
function s = strip_sep(s)
if ~isempty(s) && (s(end) == '/' || s(end) == '\'), s(end) = []; end
end


% ========================================================================
function stamp = stamp_from_name(name)
% recording_<userSlug>_<yyyyMMdd>_<HHmm>  ->  <userSlug>_<yyyyMMdd>_<HHmm>
stamp = name;
pre = 'recording_';
if startsWith(name, pre), stamp = name(numel(pre)+1:end); end
end
