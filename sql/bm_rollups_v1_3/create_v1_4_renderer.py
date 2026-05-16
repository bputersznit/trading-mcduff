# Create v1_4 renderer with gap-filling for continuous heatmap bands

import re

with open('BM_MNQ_render_bookmap_frame_v1_3.py', 'r') as f:
    content = f.read()

# Update version in header
content = re.sub(
    r'BM_MNQ_render_bookmap_frame_v1_3\.py',
    'BM_MNQ_render_bookmap_frame_v1_4.py',
    content
)

# Update function name
content = re.sub(
    r'def prepare_heatmap_matrix_with_persistence\(',
    'def prepare_heatmap_matrix_with_state_persistence(',
    content
)

# Find and replace the persistence implementation
# The key change: fill forward missing time buckets before applying EMA

old_persistence = r'''    # Apply EMA persistence across time dimension
    if persistence_alpha > 0 and persistence_alpha < 1.0:
        persisted = np.zeros_like\(raw\)
        for t_idx in range\(raw.shape\[1\]\):
            if t_idx == 0:
                persisted\[:, t_idx\] = raw\[:, t_idx\]
            else:
                persisted\[:, t_idx\] = \(
                    persistence_alpha \* persisted\[:, t_idx - 1\] \+ \(1 - persistence_alpha\) \* raw\[:, t_idx\]
                \)
        raw = persisted'''

new_persistence = '''    # Apply STATE-BASED persistence with gap-filling
    # Key change: Forward-fill gaps to create continuous bands like real Bookmap
    if persistence_alpha > 0:
        persisted = np.zeros_like(raw)
        for p_idx in range(raw.shape[0]):  # For each price level
            last_value = 0.0
            for t_idx in range(raw.shape[1]):  # For each time bucket
                if raw[p_idx, t_idx] > 0:
                    # New event: blend with persisted value
                    if t_idx == 0:
                        persisted[p_idx, t_idx] = raw[p_idx, t_idx]
                    else:
                        persisted[p_idx, t_idx] = (
                            persistence_alpha * last_value + (1 - persistence_alpha) * raw[p_idx, t_idx]
                        )
                    last_value = persisted[p_idx, t_idx]
                else:
                    # No event: decay last value (gap-filling)
                    persisted[p_idx, t_idx] = persistence_alpha * last_value
                    last_value = persisted[p_idx, t_idx]
        raw = persisted'''

content = re.sub(old_persistence, new_persistence, content, flags=re.DOTALL)

# Update call site
content = re.sub(
    r'matrix, times, prices, positive_cells = prepare_heatmap_matrix_with_persistence\(',
    'matrix, times, prices, positive_cells = prepare_heatmap_matrix_with_state_persistence(',
    content
)

with open('BM_MNQ_render_bookmap_frame_v1_4.py', 'w') as f:
    f.write(content)

print("Created BM_MNQ_render_bookmap_frame_v1_4.py with gap-filling persistence")
