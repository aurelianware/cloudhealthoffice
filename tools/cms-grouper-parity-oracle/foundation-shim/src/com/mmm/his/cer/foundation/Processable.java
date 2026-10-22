package com.mmm.his.cer.foundation;

import com.mmm.his.cer.foundation.exception.FoundationException;
import com.mmm.his.cer.foundation.transfer.IClaim;

public interface Processable<C extends IClaim, O, R extends ComponentRuntime<O>>
    extends AutoCloseable {

  void process(C claim) throws FoundationException;

  void reconfigure(R options) throws FoundationException;

  @Override
  void close();
}
