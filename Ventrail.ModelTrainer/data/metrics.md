# Ventrail heavy/light classifier - training report

Generated: 2026-10-27 04:54  
Trainer: ML.NET FastTree (16 leaves, 100 trees), seed 42  
Heavy = one saved version adds >= 1 MB after deflate (Git's zlib)  

## Dataset
- Rows: 6180 (5880 system files, 0 user-included files, 300 synthetic)
- Heavy: 248 (4.0%), light: 5932
- Extensions: 291
- Train/test split: 80/20

## Results on the held-out test set
| Metric | Value |
|---|---|
| Accuracy | 99.92 % |
| AUC | 1.0000 |
| F1 (heavy) | 0.9901 |
| Precision (heavy) | 1.0000 |
| Recall (heavy) | 0.9804 |
| 5-fold cross-validation accuracy | 99.63 % (+/- 0.22 %) |

Confusion matrix (test set; positive = heavy):

```
TEST POSITIVE RATIO:	0.0402 (51.0/(51.0+1219.0))
Confusion table
          ||======================
PREDICTED || positive | negative | Recall
TRUTH     ||======================
 positive ||       50 |        1 | 0.9804
 negative ||        0 |    1,219 | 1.0000
          ||======================
Precision ||   1.0000 |   0.9992 |
```

## Baselines (same test set)
| Rule | Accuracy | Precision (heavy) | Recall (heavy) | F1 (heavy) |
|---|---|---|---|---|
| Size >= 50 MB (Week 6 'Large' badge) | 96.22 % | 1.000 | 0.059 | 0.111 |
| Size >= 1 MB | 97.24 % | 0.593 | 1.000 | 0.745 |
| ML.NET model | 99.92 % | 1.000 | 0.980 | 0.990 |

Total time: 1202s
