function idx = build_session_index(root, opts)
%BUILD_SESSION_INDEX  One-row-per-session summary table for a data folder.
%
%   idx = BUILD_SESSION_INDEX(root) scans `root` for recording folders
%   (any subfolder containing a controlAction_*.csv, searched recursively),
%   loads each one's metadata plus a few cheap summary statistics via
%   load_recording, and returns a table with one row per session. This is
%   the backbone query layer for the controller-comparison and
%   across-players analyses: filter/group this table instead of re-scanning
%   folders in every script.
%
%   idx = BUILD_SESSION_INDEX(root, opts):
%     opts.HeavyStats  also load the continuous streams to add a couple of
%                      trace-derived columns (final difficulty, mean e_dot).
%                      Slower (reads controlAction per session). Default true.
%     opts.Quiet       suppress per-folder missing-file warnings (default true,
%                      since a batch scan expects some incomplete folders).
%
%   Columns (one row per session):
%     folder            full path to the recording folder
%     name              recording folder name
%     profileId, name_, age, physicalState   from sessionMeta (patient info;
%                       the meta 'name' is renamed name_ to avoid clashing
%                       with the folder name column)
%     isoTimestamp      session start (string, ISO 8601)
%     durationSec       activeDurationSeconds (pause-excluded)
%     controllers       controllersUsed string (';'-joined, as recorded)
%     isPI/isRuleBased/isManual   logical, from controllers
%     nNotes, nPresses  row counts from meta
%     hitRate           fraction of notes with outcome=="Hit" (NaN if no notes)
%     rReflex, rForce   the session's setpoints (targetErrorsPerMinute /
%                       targetForceMargin) from meta; NaN for older recordings
%                       predating those columns
%     finalD, meanEdot  (HeavyStats only) last logged d, mean e_dot over
%                       the session; NaN otherwise
%
%   Sessions are returned sorted by isoTimestamp (ascending) when available.
%
%   Requires base MATLAB + load_recording.m on the path. R2024b baseline.

arguments
    root (1,:) char
    opts.HeavyStats (1,1) logical = true
    opts.Quiet (1,1) logical = true
end

if ~isfolder(root)
    error('build_session_index:badRoot', '%s is not a folder.', root);
end

folders = find_recording_folders(root);
if isempty(folders)
    warning('build_session_index:none', ...
        'No recording folders (containing controlAction_*.csv) found under %s', root);
    idx = empty_index();
    return;
end

streams = {'meta','notes'};
if opts.HeavyStats, streams = [streams, {'control'}]; end

rows = cell(numel(folders), 1);
for i = 1:numel(folders)
    try
        rec = load_recording(folders{i}, 'Streams', streams, 'Quiet', opts.Quiet);
        rows{i} = summarise_one(rec, opts.HeavyStats);
    catch ME
        warning('build_session_index:skip', 'Skipping %s: %s', folders{i}, ME.message);
    end
end
rows = rows(~cellfun(@isempty, rows));
if isempty(rows)
    idx = empty_index(); return;
end

idx = vertcat(rows{:});

% Sort by start time when available.
if ismember('isoTimestamp', idx.Properties.VariableNames)
    [~, ord] = sort(idx.isoTimestamp);
    idx = idx(ord, :);
end
end % ===================== end main =====================================


% ========================================================================
function T = summarise_one(rec, heavy)
m = rec.meta;

folder   = string(rec.folder);
name     = string(rec.name);
profileId     = meta_str(m, 'profileId');
name_         = meta_str(m, 'name');
age           = meta_str(m, 'age');
physicalState = meta_str(m, 'physicalState');
isoTimestamp  = meta_str(m, 'isoTimestamp');
durationSec   = meta_num(m, 'activeDurationSeconds');
controllers   = meta_str(m, 'controllersUsed');
nNotes        = meta_num(m, 'notesWritten');
nPresses      = meta_num(m, 'pressesWritten');

isPI        = rec.hasPI;
isRuleBased = rec.hasRuleBased;
isManual    = rec.hasManual;
rReflex     = rec.rReflex;
rForce      = rec.rForce;

% hitRate from the notes table (recompute rather than trust a meta field
% that doesn't exist) — outcome is an enum-name string.
hitRate = NaN;
if ~isempty(rec.notes) && ismember('outcome', rec.notes.Properties.VariableNames)
    oc = string(rec.notes.outcome);
    oc = oc(oc ~= "" & ~ismissing(oc));
    if ~isempty(oc)
        hitRate = mean(lower(oc) == "hit");
    end
    if ~isfinite(nNotes), nNotes = numel(oc); end
end

finalD = NaN; meanEdot = NaN;
if heavy && ~isempty(rec.control)
    C = rec.control;
    if ismember('d', C.Properties.VariableNames)
        dcol = C.d(isfinite(C.d));
        if ~isempty(dcol), finalD = dcol(end); end
    end
    if ismember('e_dot', C.Properties.VariableNames)
        meanEdot = mean(C.e_dot, 'omitnan');
    end
end

T = table(folder, name, profileId, name_, age, physicalState, isoTimestamp, ...
    durationSec, controllers, isPI, isRuleBased, isManual, ...
    nNotes, nPresses, hitRate, rReflex, rForce, finalD, meanEdot);
end


% ========================================================================
function folders = find_recording_folders(root)
% Any folder (at any depth) that directly contains a controlAction_*.csv.
d = dir(fullfile(root, '**', 'controlAction_*.csv'));
folders = unique({d.folder}, 'stable');
end


% ========================================================================
function v = meta_num(m, field)
v = NaN;
if isempty(m) || ~isfield(m, field), return; end
raw = m.(field);
if isnumeric(raw), v = double(raw); else, v = str2double(string(raw)); end
end


% ========================================================================
function s = meta_str(m, field)
s = missing;
if isempty(m) || ~isfield(m, field), s = string(missing); return; end
s = string(m.(field));
end


% ========================================================================
function T = empty_index()
% Empty table with the right variable names, so downstream code that
% references columns doesn't error on a no-sessions scan.
T = table('Size',[0 19], ...
    'VariableTypes', {'string','string','string','string','string','string', ...
        'string','double','string','logical','logical','logical', ...
        'double','double','double','double','double','double','double'}, ...
    'VariableNames', {'folder','name','profileId','name_','age','physicalState', ...
        'isoTimestamp','durationSec','controllers','isPI','isRuleBased','isManual', ...
        'nNotes','nPresses','hitRate','rReflex','rForce','finalD','meanEdot'});
end
