﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using YamlDotNet.Serialization;
using Serilog;
using OpenUtau.Core.Render;
using OpenUtau.Api;

namespace OpenUtau.Core.Ustx {
    public abstract class UPart {
        public string name = "New Part";
        public string comment = string.Empty;
        public int trackNo;
        public int position = 0;

        [YamlIgnore] public virtual string DisplayName { get; }
        [YamlIgnore] public virtual int Duration { set; get; }
        [YamlIgnore] public int EndTick { get { return position + Duration; } }

        public UPart() { }

        public abstract int GetMinDurTick(UProject project);

        public virtual void BeforeSave(UProject project, UTrack track) { }
        public virtual void AfterLoad(UProject project, UTrack track) { }

        public virtual void Validate(ValidateOptions options, UProject project, UTrack track) { }

        public abstract UPart Clone();
    }

    public class UVoicePart : UPart {
        [YamlMember(Order = 100)]
        public SortedSet<UNote> notes = new SortedSet<UNote>();
        [YamlMember(Order = 101)]
        public List<UCurve> curves = new List<UCurve>();

        [YamlIgnore] public List<UPhoneme> phonemes = new List<UPhoneme>();
        [YamlIgnore] public int phonemesRevision = 0;
        [YamlIgnore] public List<RenderPhrase> renderPhrases = new List<RenderPhrase>();

        [YamlIgnore] private PhonemizerResponse phonemizerResponse;
        [YamlIgnore] private long notesTimestamp;
        [YamlIgnore] private long phonemesTimestamp;

        [YamlIgnore] public bool PhonemesUpToDate => notesTimestamp == phonemesTimestamp;

        public override string DisplayName => name;

        public override int GetMinDurTick(UProject project) {
            return notes.Count > 0
                ? Math.Max(project.BarTicks, notes.Last().End)
                : project.BarTicks;
        }

        public int GetBarDurTick(UProject project) {
            int barTicks = project.BarTicks;
            return (int)Math.Ceiling((double)GetMinDurTick(project) / barTicks) * barTicks;
        }

        public override void BeforeSave(UProject project, UTrack track) {
            foreach (var note in notes) {
                note.BeforeSave(project, track, this);
            }
        }

        public override void AfterLoad(UProject project, UTrack track) {
            foreach (var note in notes) {
                note.AfterLoad(project, track, this);
            }
            Duration = GetBarDurTick(project);
            foreach (var curve in curves) {
                if (project.expressions.TryGetValue(curve.abbr, out var descriptor)) {
                    curve.descriptor = descriptor;
                }
            }
        }

        public override void Validate(ValidateOptions options, UProject project, UTrack track) {
            UNote lastNote = null;
            foreach (UNote note in notes) {
                note.Prev = lastNote;
                note.Next = null;
                if (lastNote != null) {
                    lastNote.Next = note;
                }
                lastNote = note;
            }
            foreach (UNote note in notes) {
                note.ExtendedDuration = note.duration;
                if (note.Prev != null && note.Prev.End == note.position && note.lyric.StartsWith("+")) {
                    note.Extends = note.Prev.Extends ?? note.Prev;
                    note.Extends.ExtendedDuration = note.End - note.Extends.position;
                } else {
                    note.Extends = null;
                }
            }
            foreach (UNote note in notes) {
                note.Validate(options, project, track, this);
            }
            if (!options.SkipPhonemizer) {
                var noteIndexes = new List<int>();
                var groups = new List<Phonemizer.Note[]>();
                int noteIndex = 0;
                foreach (var note in notes) {
                    if (note.OverlapError || note.Extends != null) {
                        noteIndex++;
                        continue;
                    }
                    var group = new List<UNote>() { note };
                    var next = note.Next;
                    while (next != null && next.Extends == note) {
                        group.Add(next);
                        next = next.Next;
                    }
                    groups.Add(group.Select(e => e.ToPhonemizerNote(track)).ToArray());
                    noteIndexes.Add(noteIndex);
                    noteIndex++;
                }
                var request = new PhonemizerRequest() {
                    part = this,
                    timestamp = DateTime.Now.ToFileTimeUtc(),
                    noteIndexes = noteIndexes.ToArray(),
                    notes = groups.ToArray(),
                    phonemizer = track.Phonemizer,
                    bpm = project.bpm,
                    beatUnit = project.beatUnit,
                    resolution = project.resolution,
                };
                notesTimestamp = request.timestamp;
                DocManager.Inst.PhonemizerRunner.Push(request);
            }
            lock (this) {
                if (phonemizerResponse != null) {
                    var resp = phonemizerResponse;
                    phonemes.Clear();
                    for (int i = 0; i < resp.phonemes.Length; ++i) {
                        for (int j = 0; j < resp.phonemes[i].Length; ++j) {
                            phonemes.Add(new UPhoneme() {
                                rawPosition = resp.phonemes[i][j].position,
                                rawPhoneme = resp.phonemes[i][j].phoneme,
                                index = j,
                                Parent = notes.ElementAtOrDefault(resp.noteIndexes[i]),
                            });
                        }
                    }
                    phonemesTimestamp = resp.timestamp;
                    phonemizerResponse = null;
                }
            }
            if (!options.SkipPhoneme) {
                UPhoneme lastPhoneme = null;
                foreach (var phoneme in phonemes) {
                    phoneme.Prev = lastPhoneme;
                    phoneme.Next = null;
                    if (lastPhoneme != null) {
                        lastPhoneme.Next = phoneme;
                    }
                    lastPhoneme = phoneme;
                }
                foreach (var note in notes) {
                    for (int i = note.phonemeOverrides.Count - 1; i >= 0; --i) {
                        if (note.phonemeOverrides[i].IsEmpty) {
                            note.phonemeOverrides.RemoveAt(i);
                        }
                    }
                }
                foreach (var phoneme in phonemes) {
                    phoneme.position = phoneme.rawPosition;
                    phoneme.phoneme = phoneme.rawPhoneme;
                    phoneme.preutterDelta = null;
                    phoneme.overlapDelta = null;
                    var note = phoneme.Parent;
                    if (note == null) {
                        continue;
                    }
                    var o = note.phonemeOverrides.FirstOrDefault(o => o.index == phoneme.index);
                    if (o != null) {
                        phoneme.position += o.offset ?? 0;
                        phoneme.phoneme = o.phoneme ?? phoneme.rawPhoneme;
                        phoneme.preutterDelta = o.preutterDelta;
                        phoneme.overlapDelta = o.overlapDelta;
                    }
                }
                // Safety treatment after phonemizer output and phoneme overrides.
                for (int i = phonemes.Count - 2; i >= 0; --i) {
                    phonemes[i].position = Math.Min(phonemes[i].position, phonemes[i + 1].position - 10);
                }
                foreach (var phoneme in phonemes) {
                    var note = phoneme.Parent;
                    if (note == null) {
                        continue;
                    }
                    phoneme.Validate(options, project, track, this, note);
                }
            }
            renderPhrases.Clear();
            if (PhonemesUpToDate) {
                renderPhrases.AddRange(RenderPhrase.FromPart(project, track, this));
            }
        }

        internal void SetPhonemizerResponse(PhonemizerResponse response) {
            lock (this) {
                phonemizerResponse = response;
            }
        }

        public override UPart Clone() {
            return new UVoicePart() {
                name = name,
                comment = comment,
                trackNo = trackNo,
                position = position,
                notes = new SortedSet<UNote>(notes.Select(note => note.Clone())),
                curves = curves.Select(c => c.Clone()).ToList(),
                Duration = Duration,
            };
        }
    }

    public class UWavePart : UPart {
        string _filePath;

        [YamlIgnore]
        public string FilePath {
            set {
                _filePath = value;
                name = Path.GetFileName(value);
            }
            get { return _filePath; }
        }

        [YamlMember(Order = 100)] public string relativePath;
        [YamlMember(Order = 101)] public double fileDurationMs;
        [YamlMember(Order = 102)] public double skipMs;
        [YamlMember(Order = 103)] public double TrimMs;

        [YamlIgnore]
        public override string DisplayName => Missing ? $"[Missing] {name}" : name;
        [YamlIgnore]
        public override int Duration {
            get => fileDurTick;
            set { }
        }
        [YamlIgnore] bool Missing { get; set; }
        [YamlIgnore] public float[] Peaks { get; set; }
        [YamlIgnore] public float[] Samples { get; private set; }

        [YamlIgnore] public int channels;
        [YamlIgnore] public int fileDurTick;

        private TimeSpan duration;

        public override int GetMinDurTick(UProject project) { return project.MillisecondToTick(duration.TotalMilliseconds); }

        public override UPart Clone() {
            return new UWavePart() {
                _filePath = _filePath,
                Peaks = Peaks,
                channels = channels,
                fileDurTick = fileDurTick,
            };
        }

        private readonly object loadLockObj = new object();
        public void Load(UProject project) {
            try {
                using (var waveStream = Format.Wave.OpenFile(FilePath)) {
                    duration = waveStream.TotalTime;
                    fileDurationMs = duration.TotalMilliseconds;
                    channels = waveStream.WaveFormat.Channels;
                }
            } catch (Exception e) {
                Log.Error(e, $"Failed to load wave part {FilePath}");
                Missing = true;
                if (fileDurationMs == 0) {
                    fileDurationMs = 10000;
                }
                duration = TimeSpan.FromMilliseconds(fileDurationMs);
            }
            fileDurTick = project.MillisecondToTick(fileDurationMs);
            lock (loadLockObj) {
                if (Samples != null || Missing) {
                    return;
                }
            }
            Task.Run(() => {
                using (var waveStream = Format.Wave.OpenFile(FilePath)) {
                    var samples = Format.Wave.GetStereoSamples(waveStream);
                    lock (loadLockObj) {
                        Samples = samples;
                    }
                }
            });
        }

        public void BuildPeaks(IProgress<int> progress) {
            using (var waveStream = Format.Wave.OpenFile(FilePath)) {
                var peaks = Format.Wave.BuildPeaks(waveStream, progress);
                lock (loadLockObj) {
                    Peaks = peaks;
                }
            }
        }

        public override void Validate(ValidateOptions options, UProject project, UTrack track) {
            fileDurTick = project.MillisecondToTick(duration.TotalMilliseconds);
        }

        public override void BeforeSave(UProject project, UTrack track) {
            relativePath = Path.GetRelativePath(Path.GetDirectoryName(project.FilePath), FilePath);
        }

        public override void AfterLoad(UProject project, UTrack track) {
            FilePath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(project.FilePath), relativePath ?? ""));
            Load(project);
        }
    }
}
