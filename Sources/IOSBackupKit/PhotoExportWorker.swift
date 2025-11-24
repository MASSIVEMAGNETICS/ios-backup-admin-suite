import Foundation

#if canImport(Photos)
import Photos
#endif

#if canImport(AVFoundation)
import AVFoundation
#endif

#if canImport(Photos) && canImport(AVFoundation)
final class PhotoExportWorker {
    let batchSize: Int
    let queue = DispatchQueue(label: "photo.export.worker", attributes: .concurrent)
    private let imageManager = PHImageManager.default()

    init(batchSize: Int = 32) {
        self.batchSize = batchSize
    }

    func requestAuthorizationIfNeeded(completion: @escaping (PHAuthorizationStatus) -> Void) {
        let status = PHPhotoLibrary.authorizationStatus(for: .readWrite)
        if status == .notDetermined {
            PHPhotoLibrary.requestAuthorization(for: .readWrite) { newStatus in
                completion(newStatus)
            }
        } else {
            completion(status)
        }
    }

    func fetchAllAssets(sortedByCreation: Bool = true) -> PHFetchResult<PHAsset> {
        let opts = PHFetchOptions()
        opts.includeHiddenAssets = false
        opts.sortDescriptors = sortedByCreation ? [NSSortDescriptor(key: "creationDate", ascending: true)] : nil
        return PHAsset.fetchAssets(with: opts)
    }

    /// Export assets in batches. Handler returns (asset, data, metadata)
    func exportAllAssets(batchHandler: @escaping (_ items: [(PHAsset, Data, [String:Any])]) -> Void,
                         completion: @escaping (Result<Void, Error>) -> Void) {
        requestAuthorizationIfNeeded { status in
            guard status == .authorized || status == .limited else {
                completion(.failure(NSError(domain: "PhotoExport", code: 1, userInfo: [NSLocalizedDescriptionKey:"Photos access denied"])))
                return
            }

            let assets = self.fetchAllAssets()
            var batch: [(PHAsset, Data, [String:Any])] = []
            let group = DispatchGroup()
            var lastError: Error?

            assets.enumerateObjects { asset, idx, stop in
                group.enter()
                self.requestData(from: asset) { result in
                    switch result {
                    case .success((let data, let meta)):
                        batch.append((asset, data, meta))
                        if batch.count >= self.batchSize {
                            batchHandler(batch)
                            batch.removeAll(keepingCapacity: true)
                        }
                    case .failure(let err):
                        lastError = err
                    }
                    group.leave()
                }
            }

            group.notify(queue: .global()) {
                if !batch.isEmpty { batchHandler(batch) }
                if let err = lastError { completion(.failure(err)) } else { completion(.success(())) }
            }
        }
    }

    private func requestData(from asset: PHAsset, completion: @escaping (Result<(Data, [String:Any]), Error>) -> Void) {
        let meta: [String:Any] = [
            "localIdentifier": asset.localIdentifier,
            "creationDate": asset.creationDate as Any,
            "modificationDate": asset.modificationDate as Any,
            "mediaType": asset.mediaType.rawValue,
            "pixelWidth": asset.pixelWidth,
            "pixelHeight": asset.pixelHeight,
            "location": asset.location as Any
        ]

        if asset.mediaType == .image {
            let options = PHImageRequestOptions()
            options.isSynchronous = false
            options.deliveryMode = .highQualityFormat
            options.isNetworkAccessAllowed = true
            imageManager.requestImageDataAndOrientation(for: asset, options: options) { data, dataUTI, orientation, info in
                if let d = data { completion(.success((d, meta))) }
                else { completion(.failure(NSError(domain:"PhotoExport", code:2, userInfo:[NSLocalizedDescriptionKey:"No image data"]))) }
            }
        } else if asset.mediaType == .video {
            let kinetics = PHVideoRequestOptions()
            kinetics.isNetworkAccessAllowed = true
            imageManager.requestAVAsset(forVideo: asset, options: kinetics) { avAsset, mix, info in
                if let urlAsset = avAsset as? AVURLAsset {
                    do {
                        let d = try Data(contentsOf: urlAsset.url)
                        completion(.success((d, meta)))
                    } catch {
                        completion(.failure(error))
                    }
                } else {
                    completion(.failure(NSError(domain:"PhotoExport", code:3, userInfo:[NSLocalizedDescriptionKey:"No AVAsset URL"])))
                }
            }
        } else {
            completion(.failure(NSError(domain:"PhotoExport", code:4, userInfo:[NSLocalizedDescriptionKey:"Unsupported media type"])))
        }
    }
}
#endif
